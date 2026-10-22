using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace PortfolioManager.Core.Email;

/// <summary>The "Email" section of appsettings: the SMTP server the site sends through.</summary>
public sealed class EmailSettings
{
    /// <summary>e.g. smtp.office365.com, smtp.gmail.com or your email provider's server. Empty means no email is sent.</summary>
    public string? Host { get; set; }

    /// <summary>587 suits most providers.</summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// "Auto" (TLS from the start on port 465, otherwise STARTTLS when the server offers it), "StartTls" (insist on
    /// STARTTLS), "Tls" (TLS from the start) or "None" (a local relay or test server only).
    /// </summary>
    public string Security { get; set; } = "Auto";

    public string? UserName { get; set; }
    public string? Password { get; set; }

    /// <summary>The address emails come from, e.g. no-reply@example.com.</summary>
    public string? FromAddress { get; set; }

    /// <summary>The name emails come from. Empty uses the site name.</summary>
    public string? FromName { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

/// <summary>An email to one person. Text is the plain version; Html, when given, is what most mail apps show.</summary>
public sealed record EmailMessage(string To, string Subject, string Text, string? Html = null, string? ReplyTo = null);

public interface IEmailSender
{
    /// <summary>False when the site has no email settings, so pages can offer another way.</summary>
    bool IsConfigured { get; }

    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Sends email through an SMTP server (any provider: Microsoft 365, Gmail, SendGrid, Amazon SES and so on).</summary>
public sealed class SmtpEmailSender(EmailSettings settings, string siteName) : IEmailSender
{
    public bool IsConfigured => settings.IsConfigured;

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        using var client = new SmtpClient();
        await client.ConnectAsync(settings.Host, settings.Port, SocketOptions(settings.Security), ct);
        if (!string.IsNullOrEmpty(settings.UserName))
            await client.AuthenticateAsync(settings.UserName, settings.Password ?? "", ct);
        await client.SendAsync(Build(message, settings, siteName), ct);
        await client.DisconnectAsync(true, ct);
    }

    public static SecureSocketOptions SocketOptions(string? security) => security?.Trim().ToLowerInvariant() switch
    {
        "none" => SecureSocketOptions.None,
        "tls" or "ssl" => SecureSocketOptions.SslOnConnect,
        "starttls" => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.Auto,
    };

    public static MimeMessage Build(EmailMessage message, EmailSettings settings, string siteName)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(settings.FromName) ? siteName : settings.FromName, settings.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
            mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.Text, HtmlBody = message.Html }.ToMessageBody();
        return mime;
    }
}
