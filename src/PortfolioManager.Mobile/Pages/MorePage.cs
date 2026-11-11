namespace PortfolioManager.Mobile.Pages;

/// <summary>Settings (lock, hide in recent apps), the full site for everything else, and signing out.</summary>
public sealed class MorePage : ContentPage
{
    private readonly Session session;

    public MorePage(Session session)
    {
        this.session = session;
        Title = "More";

        var lockSwitch = new Switch { IsToggled = session.LockApp && DeviceLock.IsAvailable() };
        lockSwitch.Toggled += async (_, e) =>
        {
            if (e.Value && !DeviceLock.IsAvailable())
            {
                lockSwitch.IsToggled = false;
                await DisplayAlertAsync("No screen lock", "Set up a fingerprint, face or PIN in your phone's settings first.", "OK");
                return;
            }
            session.LockApp = e.Value;
        };
        var hideSwitch = new Switch { IsToggled = session.HideInRecents };
        hideSwitch.Toggled += (_, e) =>
        {
            session.HideInRecents = e.Value;
            DeviceLock.HideInRecents(e.Value);
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 8),
                Spacing = 4,
                Children =
                {
                    Ui.Heading("SECURITY"),
                    Setting("Lock the app", "Ask for your fingerprint, face or phone PIN when the app opens or after a minute away.", lockSwitch),
                    Setting("Hide in recent apps", "Blanks the app in the recent-apps list. On Android this also blocks screenshots.", hideSwitch),
                    Ui.Heading("THE FULL SITE"),
                    new Label { Text = "Tax returns, journals, the business books and everything else are on the site.", FontSize = 13, TextColor = Colors.Gray },
                    new Button { Text = "Open the site", Command = new Command(async () => await OpenSiteAsync()) },
                    Ui.Heading("ACCOUNT"),
                    new Label { Text = "Signed in to " + session.Server, FontSize = 13, TextColor = Colors.Gray },
                    new Button { Text = "Sign out", Command = new Command(async () => await SignOutAsync()) },
                    new Label
                    {
                        Text = $"Version {AppInfo.Current.VersionString}. Figures are for your records, not financial advice.",
                        FontSize = 12, TextColor = Colors.Gray, Margin = new Thickness(0, 16),
                    },
                },
            },
        };
    }

    private static Grid Setting(string title, string help, Switch toggle)
    {
        var grid = new Grid
        {
            Padding = new Thickness(0, 6),
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
        };
        grid.Add(new Label { Text = title, FontSize = 16 }, 0, 0);
        grid.Add(new Label { Text = help, FontSize = 13, TextColor = Colors.Gray }, 0, 1);
        grid.Add(toggle, 1, 0);
        Grid.SetRowSpan(toggle, 2);
        return grid;
    }

    private async Task OpenSiteAsync()
    {
        try
        {
            await Browser.Default.OpenAsync(session.Server, BrowserLaunchMode.SystemPreferred);
        }
        catch (Exception)
        {
            await DisplayAlertAsync("Can't open the site", "There's no web browser on this phone.", "OK");
        }
    }

    private async Task SignOutAsync()
    {
        if (!await DisplayAlertAsync("Sign out?", "The offline copy is deleted from this phone too.", "Sign out", "Cancel"))
            return;
        session.SignOut();
        ((App)Application.Current!).ShowStart();
    }
}
