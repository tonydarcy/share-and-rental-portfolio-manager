using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using PortfolioManager.Core.Data;

namespace PortfolioManager.Core.Security;

/// <summary>One entry in a login's security log (Account > Recent sign-ins). Times are UTC.</summary>
public sealed record SecurityEvent(DateTime WhenUtc, string Event, bool Success, string? IpAddress, string? UserAgent);

/// <summary>Where a sign-in or change came from: the internet address, the browser, and the browser's device cookie.</summary>
public sealed record SecurityOrigin(string? IpAddress, string? UserAgent, string? DeviceId);

/// <summary>The names written to tblSecurityEvents.strEvent. The procs count "Password", "Two-factor code" and "Signed in".</summary>
public static class SecurityEvents
{
    public const string Password = "Password";
    public const string TwoFactorCode = "Two-factor code";
    public const string SignedIn = "Signed in";
    public const string Paused = "Sign-in paused";
    public const string PasswordReset = "Password reset";
    public const string TwoFactorOn = "Two-factor turned on";
    public const string TwoFactorOff = "Two-factor turned off";
    public const string RecoveryCodes = "New recovery codes";
    public const string ApiKeyCreated = "API key created";
    public const string ApiKeyRevoked = "API key revoked";
    public const string AppConnected = "App connected";
}

/// <summary>
/// After this many wrong passwords or two-factor codes for one email within the window, sign-in for that email is paused
/// until the window has passed. This is per account, on top of the per-address limit in Program.cs.
/// </summary>
public static class SignInLockout
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public static bool IsPaused(int recentFailures) => recentFailures >= MaxFailures;

    /// <summary>True for the failure that pauses the account, so the owner is emailed once, not on every later try.</summary>
    public static bool JustPaused(int failuresIncludingThisOne) => failuresIncludingThisOne == MaxFailures;
}

/// <summary>The security log (tblSecurityEvents): sign-ins, wrong attempts and account changes.</summary>
public sealed class SecurityLogService(IDbConnectionFactory db)
{
    public const int MaxUserAgent = 300;

    public async Task AddAsync(int? userId, string? email, string evt, bool success, SecurityOrigin origin)
    {
        await using var con = db.Create();
        await con.ExecuteAsync("uspAddSecurityEvent", Parameters(userId, email, evt, success, origin),
            commandType: CommandType.StoredProcedure);
    }

    public static object Parameters(int? userId, string? email, string evt, bool success, SecurityOrigin origin) => new
    {
        iUserId = userId,
        strEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant(),
        strEvent = evt,
        bSuccess = success,
        strIpAddress = origin.IpAddress,
        strUserAgent = origin.UserAgent is { Length: > MaxUserAgent } ua ? ua[..MaxUserAgent] : origin.UserAgent,
        strDevice = DeviceHash(origin.DeviceId),
    };

    /// <summary>Wrong passwords and two-factor codes for this email since its last sign-in, within the lockout window.</summary>
    public async Task<int> RecentFailuresAsync(string email)
    {
        await using var con = db.Create();
        return await con.ExecuteScalarAsync<int>("uspCountRecentSignInFailures",
            new { strEmail = email.Trim().ToLowerInvariant(), iMinutes = (int)SignInLockout.Window.TotalMinutes },
            commandType: CommandType.StoredProcedure);
    }

    public async Task<IReadOnlyList<SecurityEvent>> RecentAsync(int userId, int count = 50)
    {
        await using var con = db.Create();
        var rows = await con.QueryAsync<Row>("uspGetSecurityEvents", new { iUserId = userId, iCount = count },
            commandType: CommandType.StoredProcedure);
        return rows.Select(r => new SecurityEvent(DateTime.SpecifyKind(r.dtmWhenUtc, DateTimeKind.Utc), r.strEvent ?? "",
            r.bSuccess, r.strIpAddress, r.strUserAgent)).ToList();
    }

    /// <summary>
    /// True when this login has signed in before but never from this device. The first sign-in after the log
    /// starts isn't "new", so turning the log on doesn't email everyone.
    /// </summary>
    public async Task<bool> IsNewDeviceAsync(int userId, string? deviceId)
    {
        if (DeviceHash(deviceId) is not { } device)
            return false;
        await using var con = db.Create();
        var history = await con.QuerySingleAsync<History>("uspGetSignInHistory", new { iUserId = userId, strDevice = device },
            commandType: CommandType.StoredProcedure);
        return history.bSignedInBefore && !history.bKnownDevice;
    }

    /// <summary>The device cookie is stored hashed, so the log can't be used to copy someone's device.</summary>
    public static string? DeviceHash(string? deviceId) => string.IsNullOrWhiteSpace(deviceId)
        ? null
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)))[..32];

    private sealed class Row
    {
        public DateTime dtmWhenUtc { get; set; }
        public string? strEvent { get; set; }
        public bool bSuccess { get; set; }
        public string? strIpAddress { get; set; }
        public string? strUserAgent { get; set; }
    }

    private sealed class History
    {
        public bool bSignedInBefore { get; set; }
        public bool bKnownDevice { get; set; }
    }
}
