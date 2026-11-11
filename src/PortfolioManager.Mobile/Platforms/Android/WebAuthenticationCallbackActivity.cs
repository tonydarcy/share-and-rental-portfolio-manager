using Android.App;
using Android.Content;
using Android.Content.PM;

namespace PortfolioManager.Mobile;

/// <summary>Catches the site's reply after sign-in (com.shareportfoliomanager.app:/oauth?code=...) and hands it to the app.</summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = Client.SignInClient.RedirectScheme)]
public class WebAuthenticationCallbackActivity : WebAuthenticatorCallbackActivity
{
}
