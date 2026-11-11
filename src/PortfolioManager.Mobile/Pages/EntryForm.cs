using System.Globalization;
using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>
/// The shared shape of the quick-entry pages: a scrolling form, a line for problems, and a Save button that sends once
/// (it can't be pressed twice while the site replies, so nothing is recorded twice).
/// </summary>
public abstract class EntryForm : ContentPage
{
    protected readonly Session Session;
    protected readonly int PortfolioId;
    protected readonly VerticalStackLayout Form = new() { Padding = new Thickness(16, 8), Spacing = 6 };
    private readonly Label problems = new() { TextColor = Colors.Firebrick, IsVisible = false };
    private readonly Button save = new() { Text = "Save", Margin = new Thickness(0, 12) };
    private readonly ActivityIndicator busy = new() { IsVisible = false, IsRunning = true };

    protected EntryForm(Session session, int portfolioId, string title)
    {
        Session = session;
        PortfolioId = portfolioId;
        Title = title;
        save.Clicked += async (_, _) => await SaveAsync();
        Content = new ScrollView { Content = Form };
    }

    /// <summary>Adds the problem line, Save button and the note; call once the form's own fields are added.</summary>
    protected void Finish(string note)
    {
        Form.Add(problems);
        Form.Add(busy);
        Form.Add(save);
        Form.Add(new Label { Text = note, FontSize = 12, TextColor = Colors.Gray });
    }

    /// <summary>Checks the form; returns problems to show, or none to go ahead.</summary>
    protected abstract IReadOnlyList<string> Check();

    /// <summary>Sends the entry; returns the message to show.</summary>
    protected abstract Task<string> SendAsync(SiteApi api);

    private async Task SaveAsync()
    {
        problems.IsVisible = false;
        var errors = Check();
        if (errors.Count > 0)
        {
            Show(string.Join("\n", errors));
            return;
        }
        save.IsEnabled = false;
        busy.IsVisible = true;
        try
        {
            var message = await SendAsync(Session.Api);
            Session.DataChanged();
            await DisplayAlertAsync("Saved", message, "OK");
            await Navigation.PopAsync();
        }
        catch (SiteException e) when (e.SignedOut)
        {
            await DisplayAlertAsync("Signed out", e.Message, "OK");
            Session.SignOut();
            ((App)Application.Current!).ShowStart();
        }
        catch (SiteException e)
        {
            Show(e.Message);
        }
        finally
        {
            save.IsEnabled = true;
            busy.IsVisible = false;
        }
    }

    private void Show(string text)
    {
        problems.Text = text;
        problems.IsVisible = true;
    }

    protected static Label Caption(string text) => new() { Text = text, FontSize = 13, TextColor = Colors.Gray, Margin = new Thickness(0, 6, 0, 0) };

    protected static Entry Amount(string placeholder = "0.00") => new() { Keyboard = Keyboard.Numeric, Placeholder = placeholder };

    /// <summary>A typed number, or 0 when blank. Read the phone's own way first (so "1,50" is 1.50 where a comma is the
    /// decimal mark), then with a point as the decimal mark.</summary>
    protected static decimal Number(Entry entry) =>
        decimal.TryParse(entry.Text?.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v)
        || decimal.TryParse(entry.Text?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v) ? v : 0;

    protected static DateOnly Day(DatePicker picker) => DateOnly.FromDateTime(picker.Date ?? DateTime.Today);

    protected static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
}
