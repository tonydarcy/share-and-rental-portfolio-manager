namespace PortfolioManager.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
        builder.Services.AddSingleton<Session>();
        builder.Services.AddTransient<Pages.SignInPage>();
        builder.Services.AddTransient<Pages.MainTabs>();
        builder.Services.AddTransient<Pages.DashboardPage>();
        builder.Services.AddTransient<Pages.HoldingsPage>();
        builder.Services.AddTransient<Pages.PayoutsPage>();
        builder.Services.AddTransient<Pages.PropertiesPage>();
        builder.Services.AddTransient<Pages.MorePage>();
        return builder.Build();
    }
}
