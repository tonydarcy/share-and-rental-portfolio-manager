using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PortfolioManager.Core.Groups;
using PortfolioManager.Core.Portfolios;
using PortfolioManager.Core.Trades;
using PortfolioManager.Web.Services;

namespace PortfolioManager.Web.Pages.Groups;

public class EditModel(CurrentPortfolio current, GroupService groups, TradesService trades) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public List<int> Ticked { get; set; } = [];

    public PortfolioSummary? Portfolio { get; private set; }
    public GroupSummary? Group { get; private set; }
    public IReadOnlyList<ShareTrade> Trades { get; private set; } = [];
    public IReadOnlySet<int> InGroup { get; private set; } = new HashSet<int>();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
            return NotFound();
        Name = Group!.Name;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
            return NotFound();
        var portfolioId = Portfolio!.PortfolioId;

        if (GroupService.ValidateName(Name) is { } error)
        {
            ModelState.AddModelError(nameof(Name), error);
            return Page();
        }
        if (Name!.Trim() != Group!.Name)
        {
            var renamed = await groups.RenameAsync(portfolioId, Id, Name);
            if (!renamed.Ok)
            {
                ModelState.AddModelError(nameof(Name), renamed.Error!);
                return Page();
            }
        }

        var changes = GroupService.Changes(Ticked, InGroup, Trades.Select(t => t.TradeId).ToHashSet());
        await groups.ApplyAsync(portfolioId, Id, changes);

        TempData["Message"] = changes.IsEmpty
            ? $"Saved the group \"{Name.Trim()}\"."
            : $"Saved the group \"{Name.Trim()}\": {changes.Add.Count} trade(s) added, {changes.Remove.Count} taken out.";
        return RedirectToPage("Index");
    }

    private async Task<bool> LoadAsync()
    {
        Portfolio = await current.GetSelectedAsync();
        if (Portfolio is null)
            return false;
        Group = (await groups.GetGroupsAsync(Portfolio.PortfolioId)).FirstOrDefault(g => g.GroupId == Id);
        if (Group is null)
            return false;
        Trades = await trades.GetTradesAsync(Portfolio.PortfolioId, null, oldestFirst: true);
        InGroup = await groups.GetTradeIdsAsync(Portfolio.PortfolioId, Id);
        return true;
    }
}
