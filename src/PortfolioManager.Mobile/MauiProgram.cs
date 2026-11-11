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
        builder.Services.AddTransient<Pages.HoldingsPage>();
        return builder.Build();
    }
}
