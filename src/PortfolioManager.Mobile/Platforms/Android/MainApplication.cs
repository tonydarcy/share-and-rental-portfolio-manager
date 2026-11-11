using Android.App;
using Android.Runtime;

namespace PortfolioManager.Mobile;

// Test (Debug) builds may use plain http to a copy of the site on the emulator's PC; Release builds are https only
#if DEBUG
[Application(NetworkSecurityConfig = "@xml/network_security_debug")]
#else
[Application(NetworkSecurityConfig = "@xml/network_security_config")]
#endif
public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
