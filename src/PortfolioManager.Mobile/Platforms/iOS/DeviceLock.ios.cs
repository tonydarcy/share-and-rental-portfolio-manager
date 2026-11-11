using LocalAuthentication;

namespace PortfolioManager.Mobile;

// Untested: the iPhone build needs a Mac (see README.md).
public static partial class DeviceLock
{
    public static partial bool IsAvailable()
    {
        using var context = new LAContext();
        return context.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthentication, out _);
    }

    public static async partial Task<bool> UnlockAsync(string reason)
    {
        using var context = new LAContext();
        var (ok, _) = await context.EvaluatePolicyAsync(LAPolicy.DeviceOwnerAuthentication, reason);
        return ok;
    }

    // Still to do on iPhone: cover the screen when the app goes to the background
    public static partial void HideInRecents(bool hide)
    {
    }
}
