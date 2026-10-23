using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace PortfolioManager.Core.Email;

/// <summary>Plain-English reasons an email couldn't be sent, for the site administrator's test page.</summary>
public static class EmailProblem
{
    public static string Describe(Exception ex) => ex switch
    {
        AuthenticationException =>
            "The email server turned down the user name or password. Check UserName and Password. Microsoft 365 needs " +
            "SMTP AUTH turned on for that mailbox, and Gmail needs an app password rather than your normal password.",
        SslHandshakeException =>
            "The secure connection to the email server failed. Try Port 587 with Security \"StartTls\", or Port 465 with Security \"Tls\".",
        SocketException or TimeoutException or OperationCanceledException =>
            "The site couldn't reach the email server. Check Host and Port, and that the server's firewall allows outgoing " +
            "connections on that port (some hosts block port 25).",
        SmtpCommandException { ErrorCode: SmtpErrorCode.SenderNotAccepted } =>
            "The email server won't send from FromAddress. Use an address the email account is allowed to send as.",
        SmtpCommandException { ErrorCode: SmtpErrorCode.RecipientNotAccepted } =>
            "The email server turned down the address the test was sent to.",
        ProtocolException or SmtpCommandException =>
            "The email server refused the message: " + ex.Message,
        MimeKit.ParseException => "One of the email addresses isn't valid: " + ex.Message,
        _ => ex.Message,
    };
}
