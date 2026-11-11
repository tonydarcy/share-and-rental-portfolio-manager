namespace PortfolioManager.Mobile;

/// <summary>Opens on the holdings when the app has a key, otherwise on the sign-in page.</summary>
public sealed class App(IServiceProvider services, Session session) : Application
{
    protected override Window CreateWindow(Microsoft.Maui.IActivationState? activationState) =>
        new(new NavigationPage(StartPage()));

    private Page StartPage() => session.IsSignedIn
        ? services.GetRequiredService<Pages.HoldingsPage>()
        : services.GetRequiredService<Pages.SignInPage>();

    /// <summary>Swaps the whole screen, so Back can't return to a signed-out page.</summary>
    public void ShowStart()
    {
        if (Windows.FirstOrDefault() is { } window)
            window.Page = new NavigationPage(StartPage());
    }
}
