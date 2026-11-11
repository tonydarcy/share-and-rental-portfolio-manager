using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Alerts;

/// <summary>A phone signed in to the app that wants alerts as notifications. The token comes from Firebase Cloud Messaging.</summary>
public sealed record PushDevice(int PushDeviceId, string Platform, string Token, string? DeviceName, DateTime Added, DateTime LastSeen);

/// <summary>One notification to show on a phone.</summary>
public sealed record PushMessage(string Title, string Body, IReadOnlyDictionary<string, string> Data);

/// <summary>Sends notifications to phones. <see cref="IsConfigured"/> is false until the site has push settings.</summary>
public interface IPushSender
{
    bool IsConfigured { get; }

    /// <summary>Sends to each token and returns the tokens the push service says are gone (app removed or signed out).</summary>
    Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default);
}

/// <summary>Used when push isn't set up: sends nothing.</summary>
public sealed class NoPushSender : IPushSender
{
    public bool IsConfigured => false;

    public Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

public static class PushRules
{
    public const string Android = "android", Ios = "ios";
    public static readonly IReadOnlyList<string> Platforms = [Android, Ios];
    public const int MaxTokenLength = 400, MaxDeviceName = 100, MaxDevicesPerLogin = 20;

    public static string? Validate(string? platform, string? token)
    {
        if (platform is null || !Platforms.Contains(platform.Trim().ToLowerInvariant()))
            return "platform must be android or ios.";
        if (string.IsNullOrWhiteSpace(token) || token.Trim().Length > MaxTokenLength || token.Any(char.IsWhiteSpace))
            return "token must be the Firebase registration token the app was given.";
        return null;
    }

    public static string? CleanName(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n))
            return null;
        return n.Length > MaxDeviceName ? n[..MaxDeviceName].Trim() : n;
    }

    /// <summary>The notification for an alert digest: the portfolio's name as the title, the alerts as the text.</summary>
    public static PushMessage ForDigest(AlertDigest d)
    {
        var title = d.Items.Count == 1 ? d.PortfolioName : $"{d.PortfolioName}: {d.Items.Count} alerts";
        var body = string.Join("\n", d.Items.Take(5).Select(i => i.Text));
        if (d.Items.Count > 5)
            body += $"\nand {d.Items.Count - 5} more";
        if (body.Length > 1000)
            body = body[..997] + "...";
        return new PushMessage(title, body, new Dictionary<string, string>
        {
            ["portfolioId"] = d.PortfolioId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["kind"] = d.Items.Select(i => i.Kind).Distinct().Count() == 1 ? d.Items[0].Kind : "Alerts",
        });
    }
}

/// <summary>
/// The phones each login has signed the app in on (tblPushDevices, 2026-11-11-push-devices.sql). A token is unique: when a
/// phone changes hands or signs in as someone else, its row moves to the new login. The app must delete its Firebase token
/// on signing out (unregister_push_device, then FirebaseMessaging.deleteToken), so the next person gets a new token and an
/// old one someone kept can't pull the phone's alerts back to their login.
/// </summary>
public sealed class PushDeviceService(IDbConnectionFactory db)
{
    public async Task<IReadOnlyList<PushDevice>> ListAsync(int loginId)
    {
        await using var con = db.Create();
        return (await con.QueryAsync<PushDevice>("""
            SELECT iPushDeviceID AS PushDeviceId, strPlatform AS Platform, strToken AS Token, strDeviceName AS DeviceName,
                   dtmAdded AS Added, dtmLastSeen AS LastSeen
            FROM tblPushDevices WHERE iLoginID = @loginId ORDER BY dtmLastSeen DESC
            """, new { loginId })).ToList();
    }

    /// <summary>Adds the phone, or refreshes it when the token is already known. Returns its id.</summary>
    public async Task<ProcResult> RegisterAsync(int loginId, string platform, string token, string? deviceName)
    {
        if (PushRules.Validate(platform, token) is { } error)
            return ProcResult.Failed(error);
        await using var con = db.Create();
        var id = await con.QuerySingleAsync<int>("""
            SET NOCOUNT ON; SET XACT_ABORT ON;
            BEGIN TRAN;
            DECLARE @id INT = (SELECT iPushDeviceID FROM tblPushDevices WITH (UPDLOCK, HOLDLOCK) WHERE strToken = @token);
            IF @id IS NULL
            BEGIN
                INSERT tblPushDevices (iLoginID, strPlatform, strToken, strDeviceName, dtmAdded, dtmLastSeen)
                VALUES (@loginId, @platform, @token, @name, SYSUTCDATETIME(), SYSUTCDATETIME());
                SET @id = CAST(SCOPE_IDENTITY() AS INT);
            END
            ELSE
                UPDATE tblPushDevices SET iLoginID = @loginId, strPlatform = @platform, strDeviceName = ISNULL(@name, strDeviceName),
                       dtmLastSeen = SYSUTCDATETIME()
                WHERE iPushDeviceID = @id;
            -- Keep the newest few per login, so a phone that reinstalls many times doesn't pile up rows
            DELETE FROM tblPushDevices WHERE iLoginID = @loginId AND iPushDeviceID NOT IN
                (SELECT TOP (@max) iPushDeviceID FROM tblPushDevices WHERE iLoginID = @loginId ORDER BY dtmLastSeen DESC);
            COMMIT;
            SELECT @id;
            """, new
        {
            loginId, platform = AnsiText.Of(platform.Trim().ToLowerInvariant()), token = AnsiText.Of(token.Trim()), name = PushRules.CleanName(deviceName),
            max = PushRules.MaxDevicesPerLogin,
        });
        return new ProcResult(true, null, id);
    }

    /// <summary>Forgets one of the login's phones, by its token or its id. False when there was no such phone.</summary>
    public async Task<bool> RemoveAsync(int loginId, string? token, int? pushDeviceId)
    {
        await using var con = db.Create();
        return await con.ExecuteAsync(
            "DELETE FROM tblPushDevices WHERE iLoginID = @loginId AND (strToken = @token OR iPushDeviceID = @pushDeviceId)",
            new { loginId, token = AnsiText.Of(token?.Trim()), pushDeviceId }) > 0;
    }

    /// <summary>Forgets tokens the push service said are gone.</summary>
    public async Task RemoveTokensAsync(IEnumerable<string> tokens)
    {
        var list = tokens.ToList();
        if (list.Count == 0)
            return;
        await using var con = db.Create();
        await con.ExecuteAsync("DELETE FROM tblPushDevices WHERE strToken = @token", list.Select(t => new { token = AnsiText.Of(t) }));
    }
}
