namespace PortfolioManager.Mobile;

/// <summary>
/// Opens on the tabs when the app has a key, otherwise on the sign-in page. With "Lock the app" on, the phone's
/// fingerprint, face or PIN is asked for on opening and after a minute away.
/// </summary>
public sealed class App(IServiceProvider services, Session session) : Application
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    protected override Window CreateWindow(Microsoft.Maui.IActivationState? activationState)
    {
        // Lock state lives with the window, so a window made again (Android recreating the app's screen) locks afresh
        var window = new Window(StartPage());
        var guard = new WindowLock(session, window);
        window.Created += (_, _) => guard.Lock();
        window.Stopped += (_, _) => guard.Left();
        window.Resumed += (_, _) => guard.Back();
        // Activated = in front and ready: only then can the phone's prompt be shown
        window.Activated += (_, _) => guard.Ask();
        window.Destroying += (_, _) => guard.Close();
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

    private sealed class WindowLock(Session session, Window window)
    {
        private DateTime? leftAt;
        private Pages.LockPage? lockPage;
        private Page? behind;
        private bool closed;

        public void Lock()
        {
            if (closed || lockPage is not null || !session.IsSignedIn || !session.LockApp || !DeviceLock.IsAvailable())
                return;
            behind = window.Page;
            lockPage = new Pages.LockPage(UnlockAsync);
            window.Page = lockPage;
        }

        public void Left()
        {
            if (lockPage is null)
                leftAt = DateTime.UtcNow;
        }

        public void Back()
        {
            if (leftAt is { } left && DateTime.UtcNow - left >= Grace)
                Lock();
            leftAt = null;
        }

        public void Ask()
        {
            if (lockPage is { } page)
                _ = page.AskAsync();
        }

        public void Close() => closed = true;

        private async Task<bool> UnlockAsync()
        {
            if (!await DeviceLock.UnlockAsync("To see your portfolio") || closed || lockPage is null)
                return false;
            window.Page = behind;
            lockPage = null;
            behind = null;
            leftAt = null;
            return true;
        }
    }
}
