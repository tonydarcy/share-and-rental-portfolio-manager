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
    private int shownPortfolio, request;
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
    }

    /// <summary>The picker, problem line and offline note, to put at the top of the page.</summary>
    protected View Header => new VerticalStackLayout { Children = { portfolios, offlineNote, message } };

    /// <summary>Loads and shows the portfolio. Check <paramref name="stillWanted"/> before showing: false means another
    /// portfolio was picked while this one loaded.</summary>
    protected abstract Task ShowAsync(SiteApi api, PortfolioSummary portfolio, Func<bool> stillWanted);

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
        if (loaded.Count == 0 || shownPortfolio != Session.LastPortfolioId)
            Refresh.IsRefreshing = true; // runs the refresh command, which loads
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
            await ShowAsync(api, picked, () => mine == request);
            if (mine != request)
                return;
            shownPortfolio = picked.PortfolioId;
            offlineNote.IsVisible = api.OfflineCopySaved is not null;
            if (api.OfflineCopySaved is { } saved)
                offlineNote.Text = $"Offline copy from {saved.ToLocalTime():d MMM h:mm tt}. Pull down to try again.";
        }
        catch (SiteException e) when (mine == request)
        {
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

    /// <summary>Opens a share's page (price chart and quote), for the portfolio whose figures are on screen.</summary>
    protected Task OpenShareAsync(string exchange, string code) =>
        Navigation.PushAsync(new SharePage(Session, shownPortfolio, exchange, code));
}
