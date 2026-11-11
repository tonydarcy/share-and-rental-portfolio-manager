using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>Type part of a share's code or name; the site's matches show below to tap.</summary>
public sealed class SharePicker : VerticalStackLayout
{
    private readonly Func<SiteApi> api;
    private readonly int portfolioId;
    private readonly Entry search = new() { Placeholder = "Code or name, e.g. CBA", Keyboard = Keyboard.Text };
    private readonly VerticalStackLayout matches = [];
    private readonly Label chosen = new() { FontAttributes = FontAttributes.Bold, IsVisible = false };
    private CancellationTokenSource? typing;

    public SharePicker(Func<SiteApi> api, int portfolioId)
    {
        this.api = api;
        this.portfolioId = portfolioId;
        Spacing = 2;
        Add(search);
        Add(chosen);
        Add(matches);
        search.TextChanged += async (_, e) => await FindAsync(e.NewTextValue);
    }

    public SecurityMatch? Picked { get; private set; }

    /// <summary>Starts with a share already picked (e.g. a holding).</summary>
    public void Pick(SecurityMatch match)
    {
        Picked = match;
        chosen.Text = $"{match.Code} ({match.Exchange})" + (match.Name is { Length: > 0 } n ? $"  {n}" : "");
        chosen.IsVisible = true;
        matches.Clear();
    }

    private async Task FindAsync(string? text)
    {
        typing?.Cancel();
        var mine = typing = new CancellationTokenSource();
        Picked = null;
        chosen.IsVisible = false;
        if (text is null || text.Trim().Length < 2)
        {
            matches.Clear();
            return;
        }
        try
        {
            await Task.Delay(350, mine.Token); // wait for typing to pause
            var found = await api().SearchSecuritiesAsync(portfolioId, text.Trim(), mine.Token);
            if (mine.IsCancellationRequested)
                return;
            matches.Clear();
            if (found.Count == 0)
                matches.Add(new Label { Text = "No match. Check the code, or add it on the site.", FontSize = 13, TextColor = Colors.Gray });
            foreach (var m in found.Take(8))
                matches.Add(new Label
                {
                    Text = $"{m.Code} ({m.Exchange})  {m.Name}", Padding = new Thickness(0, 8), FontSize = 15,
                }.OnTap(() => { Pick(m); return Task.CompletedTask; }));
        }
        catch (OperationCanceledException)
        {
        }
        catch (SiteException e)
        {
            matches.Clear();
            matches.Add(new Label { Text = e.Message, FontSize = 13, TextColor = Colors.Firebrick });
        }
    }
}
