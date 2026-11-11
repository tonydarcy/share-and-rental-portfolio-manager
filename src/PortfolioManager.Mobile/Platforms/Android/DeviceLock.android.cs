using Android.Views;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;

namespace PortfolioManager.Mobile;

public static partial class DeviceLock
{
    // Fingerprint/face, or the phone's PIN, pattern or password when there's no fingerprint (works from Android 7)
    private const int Allowed = BiometricManager.Authenticators.BiometricWeak | BiometricManager.Authenticators.DeviceCredential;

    public static partial bool IsAvailable() =>
        Platform.CurrentActivity is { } activity
        && BiometricManager.From(activity).CanAuthenticate(Allowed) == BiometricManager.BiometricSuccess;

    public static partial Task<bool> UnlockAsync(string reason)
    {
        if (Platform.CurrentActivity is not FragmentActivity activity)
            return Task.FromResult(false);
        var done = new TaskCompletionSource<bool>();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var prompt = new BiometricPrompt(activity, ContextCompat.GetMainExecutor(activity)!, new Callback(done));
                prompt.Authenticate(new BiometricPrompt.PromptInfo.Builder()
                    .SetTitle("Unlock Share Portfolio")
                    .SetSubtitle(reason)
                    .SetAllowedAuthenticators(Allowed)
                    .Build());
            }
            catch (Exception)
            {
                done.TrySetResult(false);
            }
        });
        return done.Task;
    }

    public static partial void HideInRecents(bool hide)
    {
        if (Platform.CurrentActivity?.Window is not { } window)
            return;
        if (hide)
            window.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
        else
            window.ClearFlags(WindowManagerFlags.Secure);
    }

    private sealed class Callback(TaskCompletionSource<bool> done) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result) => done.TrySetResult(true);

        // Cancelled, too many tries, or no lock set up. A single wrong finger only calls OnAuthenticationFailed and the
        // prompt stays open.
        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString) => done.TrySetResult(false);
    }
}
