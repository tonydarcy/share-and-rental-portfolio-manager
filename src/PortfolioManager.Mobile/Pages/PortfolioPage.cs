using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>
/// A tab that shows one portfolio: the portfolio picker (shared by every tab), pull down to refresh, a line for problems,
/// and an "offline copy" note when the figures came from the phone instead of the site.
/// </summary>
public abstract class PortfolioPage : ContentPage
{
    protected readonly Session Session;
    private readonly Picker portfolios = new() { Title = "Portfolio" };
    private readonly Label message = new() { IsVisible = false, Margin = new Thickness(0, 8) };
    private readonly Label offlineNote = new()
    {
        IsVisible = false, FontSize = 13, Padding = new Thickness(10, 6), Margin = new Thickness(0, 4),
        BackgroundColor = Color.FromArgb("#fff3cd"), TextColor = Color.FromArgb("#664d03"),
    };
    protected readonly RefreshView Refresh = new();
    private List<PortfolioSummary> loaded = [];
    private int shownPortfolio, shownVersion, request;
    private readonly ToolbarItem add;
    private bool busy, loadAgain;

    protected PortfolioPage(Session session, string title)
    {
        Session = session;
        Title = title;
        portfolios.SelectedIndexChanged += async (_, _) =>
        {
            if (portfolios.SelectedIndex >= 0 && !busy)
            {
                Session.LastPortfolioId = loaded[portfolios.SelectedIndex].PortfolioId;
                await ShowPortfolioAsync();
            }
        };
        Refresh.Command = new Command(async () => await LoadAsync());
        Content = Refresh;
        add = new ToolbarItem("Add", null, async () => await AddAsync());
    }

    /// <summary>The picker, problem line and offline note, to put at the top of the page.</summary>
    protected View Header => new VerticalStackLayout { Children = { portfolios, offlineNote, message } };

    /// <summary>Loads and shows the portfolio. Check <paramref name="stillWanted"/> before showing: false means another
    /// portfolio was picked while this one loaded.</summary>
    protected abstract Task ShowAsync(SiteApi api, PortfolioSummary portfolio, Func<bool> stillWanted);

    /// <summary>Removes the figures, so a portfolio that failed to load never shows the previous one's figures.</summary>
    protected abstract void ClearFigures();

    /// <summary>Loads again, e.g. after a setting on the page changed. A change during a load queues one more load.</summary>
    protected void Reload()
    {
        if (Refresh.IsRefreshing)
            loadAgain = true;
        else
            Refresh.IsRefreshing = true; // runs the refresh command, which loads
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // First visit, or the portfolio was changed on another tab
        // First visit, the portfolio was changed on another tab, or something was saved
        if (loaded.Count == 0 || shownPortfolio != Session.LastPortfolioId || shownVersion != Session.Version)
            Reload();
    }

    private async Task LoadAsync()
    {
        try
        {
            if (!await Session.LoadAsync())
            {
                SignOut();
                return;
            }
            busy = true;
            var api = Session.Api;
            loaded = await api.ListPortfoliosAsync();
            portfolios.ItemsSource = loaded.Select(p => p.Name).ToList();
            portfolios.IsVisible = loaded.Count > 1;
            var pick = loaded.FindIndex(p => p.PortfolioId == Session.LastPortfolioId);
            portfolios.SelectedIndex = loaded.Count == 0 ? -1 : Math.Max(pick, 0);
            busy = false;
            if (loaded.Count == 0)
            {
                Show("This login has no portfolios yet. Add one on the site.");
                return;
            }
            Session.LastPortfolioId = loaded[portfolios.SelectedIndex].PortfolioId;
            await ShowPortfolioAsync();
        }
        catch (SiteException e)
        {
            await Problem(e);
        }
        finally
        {
            busy = false;
            if (loadAgain)
            {
                loadAgain = false;
                Dispatcher.Dispatch(async () => await LoadAsync());
            }
            else
                Refresh.IsRefreshing = false;
        }
    }

    private async Task ShowPortfolioAsync()
    {
        message.IsVisible = false;
        var mine = ++request;
        try
        {
            var picked = loaded[portfolios.SelectedIndex];
            var api = Session.Api;
            var version = Session.Version;
            await ShowAsync(api, picked, () => mine == request);
            if (mine != request)
                return;
            shownPortfolio = picked.PortfolioId;
            shownVersion = version; // a save during the load leaves this behind, so the next visit loads again
            // Only logins that can change this portfolio get the Add button
            if (picked.CanEdit && !ToolbarItems.Contains(add))
                ToolbarItems.Add(add);
            else if (!picked.CanEdit)
                ToolbarItems.Remove(add);
            offlineNote.IsVisible = api.OfflineCopySaved is not null;
            if (api.OfflineCopySaved is { } saved)
                offlineNote.Text = $"Offline copy from {saved.ToLocalTime():d MMM h:mm tt}. Pull down to try again.";
        }
        catch (SiteException e) when (mine == request)
        {
            ClearFigures();
            shownPortfolio = 0;
            offlineNote.IsVisible = false;
            await Problem(e);
        }
        catch (SiteException)
        {
            // From a portfolio no longer picked
        }
    }

    protected async Task Problem(SiteException e)
    {
        if (e.SignedOut)
        {
            await DisplayAlertAsync("Signed out", e.Message, "OK");
            SignOut();
            return;
        }
        Show(e.Message);
    }

    protected void Show(string text)
    {
        message.Text = text;
        message.IsVisible = true;
    }

    private void SignOut()
    {
        Session.SignOut();
        ((App)Application.Current!).ShowStart();
    }

    /// <summary>The Add button: a trade, a payout or a cash entry for the portfolio on screen.</summary>
    private async Task AddAsync()
    {
        var portfolioId = shownPortfolio;
        if (portfolioId == 0 || portfolioId != Session.LastPortfolioId)
        {
            // Another portfolio was picked and hasn't loaded yet: never add to the one still on screen by mistake
            await DisplayAlertAsync("One moment", "Wait for the portfolio you picked to load, then tap Add again.", "OK");
            return;
        }
        var choice = await DisplayActionSheetAsync("Add to this portfolio", "Cancel", null, "Trade", "Payout", "Cash");
        try
        {
            var api = Session.Api;
            Page? page = choice switch
            {
                "Trade" => new AddTradePage(Session, portfolioId, (await api.GetDashboardAsync(portfolioId)).Currency ?? "AUD"),
                "Payout" => new AddPayoutPage(Session, portfolioId, (await api.GetHoldingsAsync(portfolioId, openOnly: false)).Holdings),
                "Cash" => new AddCashPage(Session, portfolioId),
                _ => null,
            };
            if (page is not null)
                await Navigation.PushAsync(page);
        }
        catch (SiteException e)
        {
            await Problem(e);
        }
    }

    /// <summary>Opens a share's page (price chart and quote), for the portfolio whose figures are on screen.</summary>
    protected Task OpenShareAsync(string exchange, string code) =>
        Navigation.PushAsync(new SharePage(Session, shownPortfolio, exchange, code));
}
