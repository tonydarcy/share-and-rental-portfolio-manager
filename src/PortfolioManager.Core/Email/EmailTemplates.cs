using System.Net;
using System.Text;

namespace PortfolioManager.Core.Email;

/// <summary>The site's emails: a short plain message with one button, in both plain text and HTML.</summary>
public static class EmailTemplates
{
    public static EmailMessage PasswordReset(string to, string siteName, string link) => Simple(to,
        $"Reset your {siteName} password",
        [$"Someone (hopefully you) asked to reset the password for {to} on {siteName}.",
         "Use the button below within the next 2 hours to choose a new password. If you didn't ask, ignore this email: your password stays as it is."],
        "Choose a new password", link);

    public static EmailMessage ConfirmEmail(string to, string siteName, string link) => Simple(to,
        $"Check your email address for {siteName}",
        [$"Thanks for signing up to {siteName}.",
         "Use the button below within the next 3 days to confirm this is your email address. You can log in once it's confirmed."],
        "Confirm my email", link);

    /// <param name="hasAccount">False when the person still has to sign up (the link goes to the sign-up page).</param>
    public static EmailMessage Invitation(string to, string siteName, string fromEmail, string portfolioName, string role, string link, bool hasAccount) => Simple(to,
        $"{fromEmail} shared \"{portfolioName}\" with you",
        [$"{fromEmail} has shared the portfolio \"{portfolioName}\" with you on {siteName}, as {role}.",
         hasAccount
             ? "Log in to open it: it's in your portfolio list."
             : $"Sign up with this email address ({to}) and log in, and the portfolio will be in your portfolio list."],
        hasAccount ? "Open it" : "Sign up", link, replyTo: fromEmail);

    public static EmailMessage Simple(string to, string subject, IReadOnlyList<string> paragraphs, string buttonText, string link,
        string? replyTo = null, string? footer = null)
    {
        var text = new StringBuilder();
        foreach (var p in paragraphs)
            text.Append(p).Append("\n\n");
        text.Append(buttonText).Append(": ").Append(link).Append('\n');
        if (footer is not null)
            text.Append('\n').Append(footer).Append('\n');

        var html = new StringBuilder("<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:15px;line-height:1.5;color:#212529;max-width:560px\">");
        foreach (var p in paragraphs)
            html.Append("<p>").Append(WebUtility.HtmlEncode(p)).Append("</p>");
        html.Append("<p><a href=\"").Append(WebUtility.HtmlEncode(link))
            .Append("\" style=\"display:inline-block;background:#0d6efd;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none\">")
            .Append(WebUtility.HtmlEncode(buttonText)).Append("</a></p>")
            .Append("<p style=\"font-size:13px;color:#6c757d\">Or copy this address into your browser: ").Append(WebUtility.HtmlEncode(link)).Append("</p>");
        if (footer is not null)
            html.Append("<p style=\"font-size:13px;color:#6c757d\">").Append(WebUtility.HtmlEncode(footer)).Append("</p>");
        html.Append("</div>");
        return new EmailMessage(to, subject, text.ToString(), html.ToString(), replyTo);
    }
}
