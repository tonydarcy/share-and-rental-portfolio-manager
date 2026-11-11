using PortfolioManager.Mobile.Client;

namespace PortfolioManager.Mobile.Pages;

/// <summary>Records a buy or sale, optionally with a photo of the contract note attached to it.</summary>
public sealed class AddTradePage : EntryForm
{
    private readonly Picker type = new() { ItemsSource = new List<string> { "Buy", "Sell" }, SelectedIndex = 0 };
    private readonly SharePicker share;
    private readonly DatePicker date = new() { Date = DateTime.Today, MaximumDate = DateTime.Today };
    private readonly Entry units = new() { Keyboard = Keyboard.Numeric, Placeholder = "0" };
    private readonly Entry price = Amount();
    private readonly Entry brokerage = Amount();
    private readonly Entry comment = new() { Placeholder = "Optional" };
    private readonly Switch recordCash = new();
    private readonly Label total = new() { FontSize = 15 };
    private readonly Label photoName = new() { FontSize = 13, TextColor = Colors.Gray };
    private readonly string currency;
    private (string Name, byte[] Bytes)? photo;

    public AddTradePage(Session session, int portfolioId, string currency, SecurityMatch? start = null)
        : base(session, portfolioId, "Add trade")
    {
        this.currency = currency;
        share = new SharePicker(() => Session.Api, portfolioId);
        if (start is not null)
            share.Pick(start);
        foreach (var e in new[] { units, price, brokerage })
            e.TextChanged += (_, _) => ShowTotal();
        type.SelectedIndexChanged += (_, _) => ShowTotal();

        Form.Add(Caption("Buy or sell"));
        Form.Add(type);
        Form.Add(Caption("Share"));
        Form.Add(share);
        Form.Add(Caption("Trade date"));
        Form.Add(date);
        Form.Add(Caption("Units"));
        Form.Add(units);
        Form.Add(Caption($"Price per unit ({currency})"));
        Form.Add(price);
        Form.Add(Caption("Brokerage"));
        Form.Add(brokerage);
        Form.Add(total);
        Form.Add(Line("Also record the bank payment", recordCash));
        Form.Add(Caption("Note"));
        Form.Add(comment);
        Form.Add(Caption("Contract note"));
        Form.Add(new Button { Text = "Add a photo", Command = new Command(async () => await TakePhotoAsync()) });
        Form.Add(photoName);
        Finish($"Saved the same way as the site's Add trade page, with its journals. Trades in another currency: use the site.");
    }

    private TradeRequest Request() => new(
        Day(date), (string)type.SelectedItem, share.Picked?.Exchange ?? "", share.Picked?.Code ?? "",
        Number(units), Number(price), Number(brokerage), currency, 1, NullIfBlank(comment.Text), recordCash.IsToggled);

    protected override IReadOnlyList<string> Check() => EntryChecks.Trade(Request(), Today);

    protected override async Task<string> SendAsync(SiteApi api)
    {
        var saved = await api.AddTradeAsync(PortfolioId, Request());
        var message = saved.Message ?? "Trade saved.";
        if (photo is { } p && saved.TradeId is int id and > 0)
        {
            try
            {
                await api.AddAttachmentAsync(PortfolioId, new AttachmentRequest("Trade", id, p.Name, Convert.ToBase64String(p.Bytes), "Contract note"));
                message += " The photo is attached.";
            }
            catch (SiteException e)
            {
                // The trade is saved; only the photo failed. Don't let Save be pressed again for the trade.
                message += $" The photo wasn't attached ({e.Message}) You can attach it to the trade on the site.";
            }
        }
        return message;
    }

    private void ShowTotal()
    {
        var t = TradeTotal((string)type.SelectedItem, Number(units), Number(price), Number(brokerage));
        total.Text = t == 0 ? "" : $"{((string)type.SelectedItem == "Buy" ? "Cost" : "Proceeds")} {Ui.Money(t)} {currency}";
    }

    /// <summary>Units × price, plus brokerage on a buy or less it on a sale.</summary>
    private static decimal TradeTotal(string type, decimal units, decimal price, decimal brokerage) =>
        units * price + (type == "Buy" ? brokerage : -brokerage);

    private async Task TakePhotoAsync()
    {
        var choice = await DisplayActionSheetAsync("Contract note", "Cancel", null, "Take a photo", "Choose a photo");
        try
        {
            var file = choice switch
            {
                "Take a photo" when MediaPicker.Default.IsCaptureSupported => await MediaPicker.Default.CapturePhotoAsync(),
                "Choose a photo" => (await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 1 }))?.FirstOrDefault(),
                _ => null,
            };
            if (file is null)
                return;
            await using var stream = await file.OpenReadAsync();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            if (copy.Length > EntryChecks.MaxAttachmentBytes)
            {
                await DisplayAlertAsync("Photo too big", "The site takes files up to 10 MB.", "OK");
                return;
            }
            photo = (file.FileName, copy.ToArray());
            photoName.Text = $"{file.FileName} ({copy.Length / 1024:N0} KB) will be attached when you save.";
        }
        catch (PermissionException)
        {
            await DisplayAlertAsync("No access", "Allow the camera or photos for this app in your phone's settings.", "OK");
        }
    }

    private static Grid Line(string text, View control)
    {
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, Padding = new Thickness(0, 6) };
        grid.Add(new Label { Text = text, VerticalTextAlignment = TextAlignment.Center }, 0, 0);
        grid.Add(control, 1, 0);
        return grid;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
