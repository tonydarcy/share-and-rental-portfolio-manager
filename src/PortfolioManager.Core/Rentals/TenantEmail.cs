using System.Net;
using System.Text;
using PortfolioManager.Core.Email;

namespace PortfolioManager.Core.Rentals;

/// <summary>Rent receipts and arrears reminders emailed to a tenant, from the landlord (replies go to the landlord, not the site).</summary>
public static class TenantEmail
{
    /// <summary>A receipt for one rent payment, with what the rent is paid to afterwards.</summary>
    public static EmailMessage Receipt(Tenancy t, RentPayment payment, RentPosition position, string address, string landlord, string? replyTo)
    {
        var paragraphs = new List<string>
        {
            $"Hi {t.Tenants},",
            $"Thank you for your rent payment of {payment.Amount:C}, received on {payment.Paid:d MMMM yyyy}, for {address}.",
        };
        if (position.PaidTo is { } paidTo)
            paragraphs.Add($"Your rent is paid to {paidTo:d MMMM yyyy}.");
        if (position.Arrears > 0)
            paragraphs.Add($"There is still {position.Arrears:C} owing.");
        else if (position.Credit > 0)
            paragraphs.Add($"You are {position.Credit:C} in advance.");
        var details = $"Receipt for rent\nProperty: {address}\nTenant: {t.Tenants}\nDate received: {payment.Paid:d MMMM yyyy}\nAmount: {payment.Amount:C}\n"
                      + (payment.Note is { Length: > 0 } n ? $"Note: {n}\n" : "") + $"Received by: {landlord}";
        return Build(t, $"Rent receipt: {payment.Amount:C} received {payment.Paid:d MMM yyyy}", paragraphs, details, landlord, replyTo);
    }

    /// <summary>A polite reminder of rent owing, or null when nothing is owing.</summary>
    public static EmailMessage? Reminder(Tenancy t, RentPosition position, DateOnly today, string address, string landlord, string? replyTo, string? message = null)
    {
        if (position.Arrears <= 0)
            return null;
        var paragraphs = new List<string>
        {
            $"Hi {t.Tenants},",
            $"Our records show {position.Arrears:C} of rent owing for {address}" +
                (position.OldestUnpaid is { } d ? $", from {d:d MMMM yyyy} ({position.DaysBehind(today)} days)." : "."),
            "If you have already paid, thank you, and please let us know the date so we can check our records. Otherwise please pay as soon as you can, or get in touch if you need to talk about it.",
        };
        if (!string.IsNullOrWhiteSpace(message))
            paragraphs.Add(message.Trim());
        var details = $"Rent {t.Rent:C} {RentFrequencies.Text(t.Frequency)}\nDue to {today:d MMMM yyyy}: {position.Due:C}\nPaid: {position.Paid:C}\nOwing: {position.Arrears:C}"
                      + (position.PaidTo is { } pt ? $"\nPaid to: {pt:d MMMM yyyy}" : "");
        return Build(t, $"Rent reminder: {position.Arrears:C} owing for {address}", paragraphs, details, landlord, replyTo);
    }

    private static EmailMessage Build(Tenancy t, string subject, IReadOnlyList<string> paragraphs, string details, string landlord, string? replyTo)
    {
        var text = new StringBuilder();
        foreach (var p in paragraphs)
            text.Append(p).Append("\n\n");
        text.Append(details).Append("\n\n").Append(landlord).Append('\n');

        static string E(string? s) => WebUtility.HtmlEncode(s ?? "").Replace("\n", "<br>");
        var html = new StringBuilder("<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:15px;line-height:1.5;color:#212529;max-width:640px\">");
        foreach (var p in paragraphs)
            html.Append("<p>").Append(E(p)).Append("</p>");
        html.Append("<p style=\"border:1px solid #dee2e6;padding:8px 12px\">").Append(E(details)).Append("</p>")
            .Append("<p>").Append(E(landlord)).Append("</p></div>");
        return new EmailMessage(t.Email!, subject, text.ToString(), html.ToString(), replyTo);
    }
}
