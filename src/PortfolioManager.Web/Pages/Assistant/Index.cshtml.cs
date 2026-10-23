using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Api;
using PortfolioManager.Core.Assistant;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Web.Api;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Assistant;

/// <summary>
/// The assistant: a chat about the portfolio being looked at, using the same tools as the API. Changes wait here for the
/// person to approve or reject them. Each login uses its own Anthropic API key (kept encrypted), or the host's if set.
/// </summary>
[AllowViewOnly]
public class IndexModel(CurrentPortfolio current, AssistantChat chat, AssistantService store, AgentTools tools) : PageModel
{
    public const int MaxMessage = 4000;

    [BindProperty] public string? Message { get; set; }
    [BindProperty] public string? ApiKey { get; set; }
    [BindProperty] public string? ChosenModel { get; set; }

    public PortfolioSummary? Portfolio { get; private set; }
    public AssistantTranscript Transcript { get; private set; } = AssistantTranscript.Empty();
    public IReadOnlyList<AssistantToolUse> Waiting { get; private set; } = [];
    public AssistantSettings Settings { get; private set; } = AssistantSettings.None;
    public bool HasKey { get; private set; }
    public bool HostKey => chat.HostKey is not null;
    public string? Problem { get; private set; }

    public AgentTool? Tool(string name) => tools.Find(name);

    private int LoginId => CurrentPortfolio.UserId(User);
    private ApiCaller Caller => AssistantChat.Caller(LoginId, User.Identity?.Name ?? "");

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostSendAsync(CancellationToken ct)
    {
        if (await LoadAsync() is not { } p)
            return Forbid();
        var text = Message?.Trim();
        if (string.IsNullOrEmpty(text))
            return Page();
        if (text.Length > MaxMessage)
        {
            Problem = $"Keep a message under {MaxMessage:N0} characters.";
            return Page();
        }
        return Show(await chat.SendAsync(Caller, p, text, ct), text);
    }

    public async Task<IActionResult> OnPostApproveAsync(CancellationToken ct) => await DecideAsync(true, ct);

    public async Task<IActionResult> OnPostRejectAsync(CancellationToken ct) => await DecideAsync(false, ct);

    public async Task<IActionResult> OnPostClearAsync()
    {
        if (await LoadAsync() is not { } p)
            return Forbid();
        await chat.ClearAsync(LoginId, p.PortfolioId);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSettingsAsync()
    {
        var key = ApiKey?.Trim();
        if (key is { Length: > 0 } && (!key.StartsWith("sk-ant-", StringComparison.Ordinal) || key.Length > 300 || key.Any(char.IsWhiteSpace)))
        {
            TempData["Error"] = "That doesn't look like an Anthropic API key. They start with sk-ant- and come from console.anthropic.com.";
            return RedirectToPage(null, null, "settings");
        }
        var r = await store.SaveSettingsAsync(LoginId, key is { Length: > 0 } ? chat.Protect(key) : null, key is { Length: > 0 } ? "…" + key[^4..] : null,
            AssistantModels.Pick(ChosenModel));
        TempData[r.Ok ? "Message" : "Error"] = r.Ok ? (key is { Length: > 0 } ? "API key saved." : "Settings saved.") : r.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveKeyAsync()
    {
        var settings = await store.GetSettingsAsync(LoginId);
        var r = await store.SaveSettingsAsync(LoginId, null, null, AssistantModels.Pick(settings.Model), clearKey: true);
        TempData[r.Ok ? "Message" : "Error"] = r.Ok ? "Your API key was removed from this site." : r.Error;
        return RedirectToPage();
    }

    private async Task<IActionResult> DecideAsync(bool approve, CancellationToken ct)
    {
        if (await LoadAsync() is not { } p)
            return Forbid();
        return Show(await chat.DecideAsync(Caller, p, approve, ct), null);
    }

    private IActionResult Show(AssistantTurn turn, string? typed)
    {
        Transcript = turn.Transcript;
        Waiting = turn.Waiting;
        Problem = turn.Error;
        // Keep what was typed when it couldn't be sent
        Message = turn.Error is not null && typed is not null ? typed : null;
        ModelState.Remove(nameof(Message));
        return Page();
    }

    private async Task<PortfolioSummary?> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        Settings = await store.GetSettingsAsync(LoginId);
        HasKey = Settings.ApiKeyProtected is not null || HostKey;
        if (Portfolio is not null)
        {
            Transcript = await store.GetChatAsync(LoginId, Portfolio.PortfolioId);
            Waiting = Transcript.PendingToolUses();
        }
        return Portfolio;
    }
}
