using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

public sealed class SignInPage : ContentPage
{
    private readonly Session session;
    private readonly IServiceProvider services;
    private readonly Entry server;
    private readonly Button signIn;
    private readonly Label message;

    public SignInPage(Session session, IServiceProvider services)
    {
        this.session = session;
        this.services = services;
        Title = "Sign in";
        server = new Entry { Text = session.Server, Keyboard = Keyboard.Url, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        signIn = new Button { Text = "Sign in" };
        signIn.Clicked += async (_, _) => await SignInAsync();
        message = new Label { TextColor = Colors.Firebrick, IsVisible = false };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 16,
                Children =
                {
                    new Label { Text = "Share Portfolio Manager", FontSize = 24, FontAttributes = FontAttributes.Bold },
                    new Label { Text = "Sign in with your site account. The site's sign-in page opens in your browser, so your password and two-step code stay with the site." },
                    new Label { Text = "Site address", FontAttributes = FontAttributes.Bold },
                    server,
                    signIn,
                    message,
                },
            },
        };
    }

    private async Task SignInAsync()
    {
        message.IsVisible = false;
        var address = ServerAddress.Normalise(server.Text);
        if (address is null)
        {
            Show("Type the site address, e.g. app.shareportfoliomanager.com");
            return;
        }

        signIn.IsEnabled = false;
        signIn.Text = "Signing in...";
        try
        {
            await session.SignInAsync(address);
            ((App)Application.Current!).ShowStart();
        }
        catch (SiteException e)
        {
            Show(e.Message);
        }
        catch (HttpRequestException)
        {
            Show("Can't reach the site. Check the address and your internet connection.");
        }
        catch (Exception e)
        {
            Show("Sign-in didn't work: " + e.Message);
        }
        finally
        {
            signIn.IsEnabled = true;
            signIn.Text = "Sign in";
        }
    }

    private void Show(string text)
    {
        message.Text = text;
        message.IsVisible = true;
    }
}
