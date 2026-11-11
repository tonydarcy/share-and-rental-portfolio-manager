using Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific;

namespace PortfolioManager.Mobile.Pages;

/// <summary>The signed-in app: tabs along the bottom.</summary>
public sealed class MainTabs : Microsoft.Maui.Controls.TabbedPage
{
    public MainTabs(DashboardPage dashboard, HoldingsPage holdings, PayoutsPage payouts, PropertiesPage properties, MorePage more)
    {
        On<Microsoft.Maui.Controls.PlatformConfiguration.Android>().SetToolbarPlacement(ToolbarPlacement.Bottom);
        foreach (var page in new Page[] { dashboard, holdings, payouts, properties, more })
            Children.Add(new NavigationPage(page) { Title = page.Title });
    }
}
