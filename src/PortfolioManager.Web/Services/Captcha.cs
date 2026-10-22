using System.Text.Json;

namespace PortfolioManager.Web.Services;

/// <summary>The "Captcha" section of appsettings: Cloudflare Turnstile keys (free, from the Cloudflare dashboard).</summary>
public sealed class CaptchaSettings
{
    public string? SiteKey { get; set; }
    public string? SecretKey { get; set; }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(SiteKey) && !string.IsNullOrWhiteSpace(SecretKey);
}

/// <summary>
/// The "are you human" check on sign-up, login and the email forms, using Cloudflare Turnstile. Off unless both keys
/// are set. Pages show it with the _Captcha partial and check it with <see cref="VerifyAsync"/>.
/// </summary>
public sealed class Captcha(CaptchaSettings settings, HttpClient http, ILogger<Captcha> logger)
{
    public const string FormField = "cf-turnstile-response";
    public const string Failed = "Please tick the \"I'm human\" check and try again.";
    private static readonly Uri VerifyUrl = new("https://challenges.cloudflare.com/turnstile/v0/siteverify");

    public bool IsEnabled => settings.IsEnabled;
    public string? SiteKey => settings.SiteKey;

    /// <summary>True when the check is off, or Cloudflare confirms the form's answer.</summary>
    public async Task<bool> VerifyAsync(HttpContext context)
    {
        if (!IsEnabled)
            return true;
        var answer = context.Request.HasFormContentType ? context.Request.Form[FormField].ToString() : "";
        if (string.IsNullOrEmpty(answer))
            return false;
        try
        {
            using var response = await http.PostAsync(VerifyUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"] = settings.SecretKey!,
                ["response"] = answer,
                ["remoteip"] = context.Connection.RemoteIpAddress?.ToString() ?? "",
            }), context.RequestAborted);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.RequestAborted));
            return json.RootElement.TryGetProperty("success", out var success) && success.GetBoolean();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Couldn't reach Cloudflare to check a CAPTCHA");
            return false;
        }
    }
}
