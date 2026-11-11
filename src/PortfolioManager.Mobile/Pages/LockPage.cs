namespace PortfolioManager.Mobile.Pages;

/// <summary>
/// Covers the app until the phone's fingerprint, face or PIN prompt is passed. The app asks once the screen is in front
/// (<see cref="AskAsync"/>); the Unlock button asks again.
/// </summary>
public sealed class LockPage : ContentPage
{
    private readonly Func<Task<bool>> unlock;
    private bool asking;

    public LockPage(Func<Task<bool>> unlock)
    {
        this.unlock = unlock;
        BackgroundColor = Color.FromArgb("#212529");
        Content = new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Spacing = 16,
            Padding = 32,
            Children =
            {
                new Label { Text = "Share Portfolio", FontSize = 26, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center },
                new Label { Text = "Locked", FontSize = 15, TextColor = Colors.LightGray, HorizontalTextAlignment = TextAlignment.Center },
                new Button { Text = "Unlock", Command = new Command(async () => await AskAsync()) },
            },
        };
    }

    public async Task AskAsync()
    {
        if (asking)
            return;
        asking = true;
        try
        {
            await unlock();
        }
        finally
        {
            asking = false;
        }
    }
}
