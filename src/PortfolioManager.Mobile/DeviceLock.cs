namespace PortfolioManager.Mobile;

/// <summary>The phone's own lock: fingerprint, face or the phone's PIN, and hiding the app in the recent-apps list.</summary>
public static partial class DeviceLock
{
    /// <summary>Whether the phone has a screen lock or fingerprint/face set up that the app can ask for.</summary>
    public static partial bool IsAvailable();

    /// <summary>Shows the phone's unlock prompt. True once the person has unlocked.</summary>
    public static partial Task<bool> UnlockAsync(string reason);

    /// <summary>Blanks the app in the recent-apps list (on Android this also blocks screenshots).</summary>
    public static partial void HideInRecents(bool hide);
}
