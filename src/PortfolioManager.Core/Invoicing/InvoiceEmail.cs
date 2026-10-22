using System.Net;
using System.Text;
using PortfolioManager.Core.Email;

namespace PortfolioManager.Core.Invoicing;

/// <summary>An approved invoice as an email to the customer: its lines, totals, due date and how to pay.</summary>
public static class InvoiceEmail
{
    /// <param name="replyTo">The business's own email, so the customer's reply goes to it rather than the site.</param>
    public static EmailMessage Build(InvoiceDetail invoice, InvoiceSettings settings, string businessName, string taxName,
        string to, string? replyTo, string? message = null)
    {
        var greeting = $"Hi {(string.IsNullOrWhiteSpace(invoice.Customer.ContactName) ? invoice.Customer.Name : invoice.Customer.ContactName.Trim())},";
        var intro = $"Here is invoice {invoice.Number} from {businessName} for {invoice.Total:C}"
            + (invoice.Balance > 0 && invoice.Balance != invoice.Total ? $" ({invoice.Balance:C} still to pay)" : "")
            + $", due on {invoice.DueDate:d}.";
        var paragraphs = new List<string> { greeting, intro };
        if (!string.IsNullOrWhiteSpace(message))
            paragraphs.Add(message.Trim());

        var text = new StringBuilder();
        foreach (var p in paragraphs)
            text.Append(p).Append("\n\n");
        text.Append($"Invoice {invoice.Number}, issued {invoice.IssueDate:d}");
        if (!string.IsNullOrWhiteSpace(invoice.Reference))
            text.Append($", reference {invoice.Reference}");
        text.Append("\n\n");
        foreach (var line in invoice.Lines)
            text.Append($"{line.Description}  {line.Quantity:0.##} x {line.UnitPrice:C}  {line.Total:C}\n");
        text.Append($"\nSubtotal: {invoice.Net:C}\n{taxName}: {invoice.Tax:C}\nTotal: {invoice.Total:C}\n");
        if (invoice.Paid > 0)
            text.Append($"Paid: {invoice.Paid:C}\nTo pay: {invoice.Balance:C}\n");
        if (!string.IsNullOrWhiteSpace(settings.PaymentDetails))
            text.Append("\nHow to pay:\n").Append(settings.PaymentDetails.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(invoice.Notes))
            text.Append('\n').Append(invoice.Notes.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(settings.Footer))
            text.Append('\n').Append(settings.Footer.Trim()).Append('\n');

        static string E(string? s) => WebUtility.HtmlEncode(s ?? "").Replace("\n", "<br>");
        const string cell = "padding:4px 8px;border-bottom:1px solid #dee2e6";
        var html = new StringBuilder("<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:15px;line-height:1.5;color:#212529;max-width:640px\">");
        foreach (var p in paragraphs)
            html.Append("<p>").Append(E(p)).Append("</p>");
        html.Append($"<h2 style=\"font-size:18px\">{E(businessName)}: invoice {E(invoice.Number)}</h2>")
            .Append($"<p>Issued {invoice.IssueDate:d}, due {invoice.DueDate:d}")
            .Append(string.IsNullOrWhiteSpace(invoice.Reference) ? "" : $", reference {E(invoice.Reference)}").Append("</p>")
            .Append("<table style=\"border-collapse:collapse;width:100%\"><tr>")
            .Append($"<th style=\"{cell};text-align:left\">Description</th><th style=\"{cell};text-align:right\">Qty</th>")
            .Append($"<th style=\"{cell};text-align:right\">Price</th><th style=\"{cell};text-align:right\">Amount</th></tr>");
        foreach (var line in invoice.Lines)
            html.Append($"<tr><td style=\"{cell}\">{E(line.Description)}</td><td style=\"{cell};text-align:right\">{line.Quantity:0.##}</td>")
                .Append($"<td style=\"{cell};text-align:right\">{E(line.UnitPrice.ToString("C"))}</td><td style=\"{cell};text-align:right\">{E(line.Total.ToString("C"))}</td></tr>");
        void Total(string label, decimal amount, bool bold = false) =>
            html.Append($"<tr><td colspan=\"3\" style=\"padding:4px 8px;text-align:right{(bold ? ";font-weight:bold" : "")}\">{E(label)}</td>")
                .Append($"<td style=\"padding:4px 8px;text-align:right{(bold ? ";font-weight:bold" : "")}\">{E(amount.ToString("C"))}</td></tr>");
        Total("Subtotal", invoice.Net);
        Total(taxName, invoice.Tax);
        Total("Total", invoice.Total, bold: invoice.Paid == 0);
        if (invoice.Paid > 0)
        {
            Total("Paid", invoice.Paid);
            Total("To pay", invoice.Balance, bold: true);
        }
        html.Append("</table>");
        if (!string.IsNullOrWhiteSpace(settings.PaymentDetails))
            html.Append("<p><strong>How to pay</strong><br>").Append(E(settings.PaymentDetails.Trim())).Append("</p>");
        if (!string.IsNullOrWhiteSpace(invoice.Notes))
            html.Append("<p>").Append(E(invoice.Notes.Trim())).Append("</p>");
        if (!string.IsNullOrWhiteSpace(settings.Footer))
            html.Append("<p style=\"font-size:13px;color:#6c757d\">").Append(E(settings.Footer.Trim())).Append("</p>");
        html.Append("</div>");

        return new EmailMessage(to, $"Invoice {invoice.Number} from {businessName}", text.ToString(), html.ToString(), replyTo);
    }
}
