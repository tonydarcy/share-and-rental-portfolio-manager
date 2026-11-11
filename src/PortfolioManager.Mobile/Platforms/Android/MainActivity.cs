using Android.App;
using Android.Content.PM;

namespace PortfolioManager.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Blank in the recent-apps list (and no screenshots) unless turned off under More
        if (Preferences.Default.Get("hide_in_recents", true))
            Window?.SetFlags(Android.Views.WindowManagerFlags.Secure, Android.Views.WindowManagerFlags.Secure);
    }
}
