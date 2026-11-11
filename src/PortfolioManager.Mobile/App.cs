namespace PortfolioManager.Mobile;

/// <summary>
/// Opens on the tabs when the app has a key, otherwise on the sign-in page. With "Lock the app" on, the phone's
/// fingerprint, face or PIN is asked for on opening and after a minute away.
/// </summary>
public sealed class App(IServiceProvider services, Session session) : Application
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);
    private DateTime? leftAt;
    private bool locked, started;

    protected override Window CreateWindow(Microsoft.Maui.IActivationState? activationState)
    {
        var window = new Window(StartPage());
        // First time on screen (the phone's prompt needs the app in front)
        window.Activated += (_, _) =>
        {
            if (!started)
            {
                started = true;
                Lock(window);
            }
        };
        window.Stopped += (_, _) =>
        {
            if (!locked)
                leftAt = DateTime.UtcNow;
        };
        window.Resumed += (_, _) =>
        {
            if (leftAt is { } left && DateTime.UtcNow - left >= Grace)
                Lock(window);
            leftAt = null;
        };
        return window;
    }

    private Page StartPage() => session.IsSignedIn
        ? services.GetRequiredService<Pages.MainTabs>()
        : new NavigationPage(services.GetRequiredService<Pages.SignInPage>());

    /// <summary>Swaps the whole screen, so Back can't return to a signed-out page.</summary>
    public void ShowStart()
    {
        if (Windows.FirstOrDefault() is { } window)
            window.Page = StartPage();
    }

    private void Lock(Window window)
    {
        if (locked || !session.IsSignedIn || !session.LockApp || !DeviceLock.IsAvailable())
            return;
        locked = true;
        var behind = window.Page;
        window.Page = new Pages.LockPage(async () =>
        {
            if (!await DeviceLock.UnlockAsync("To see your portfolio"))
                return false;
            locked = false;
            leftAt = null;
            window.Page = behind;
            return true;
        });
    }
}
