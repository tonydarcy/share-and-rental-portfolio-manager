using System.Security.Cryptography;
using System.Text;

namespace PortfolioManager.Core.Security;

/// <summary>
/// Authenticator app codes (TOTP, RFC 6238: six digits that change every 30 seconds) and one-off recovery codes,
/// for two-factor sign-in. Works with Google Authenticator, Microsoft Authenticator, 1Password and the like.
/// </summary>
public static class TwoFactor
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int StepSeconds = 30;
    public const int RecoveryCodeCount = 10;

    /// <summary>A new random secret, in the Base32 form authenticator apps take.</summary>
    public static string NewSecret() => ToBase32(RandomNumberGenerator.GetBytes(20));

    /// <summary>The otpauth:// address an authenticator app reads from the QR code.</summary>
    public static string AuthenticatorUri(string issuer, string account, string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
        + $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period={StepSeconds}";

    /// <summary>The six-digit code for a moment in time.</summary>
    public static string Code(string secret, DateTimeOffset at) => Code(FromBase32(secret), at.ToUnixTimeSeconds() / StepSeconds);

    /// <summary>
    /// True when the code matches now, or the 30 seconds either side (phone clocks drift).
    /// Spaces in the code are ignored, as apps often show "123 456".
    /// </summary>
    public static bool VerifyCode(string? secret, string? code, DateTimeOffset now)
    {
        code = Clean(code);
        if (string.IsNullOrEmpty(secret) || code is not { Length: 6 } || !code.All(char.IsAsciiDigit))
            return false;
        var key = FromBase32(secret);
        var step = now.ToUnixTimeSeconds() / StepSeconds;
        var matched = false;
        for (var offset = -1; offset <= 1; offset++)
            matched |= CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(key, step + offset)), Encoding.ASCII.GetBytes(code));
        return matched;
    }

    private static string Code(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--, step >>= 8)
            counter[i] = (byte)(step & 0xff);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// New recovery codes to show the person once, and how they are stored (SHA-256 hashes, separated by ';').
    /// Each code works once, in place of an authenticator code, for when the phone is lost.
    /// </summary>
    public static (IReadOnlyList<string> Codes, string Stored) NewRecoveryCodes()
    {
        var codes = Enumerable.Range(0, RecoveryCodeCount)
            .Select(_ => RandomNumberGenerator.GetString("abcdefghjkmnpqrstuvwxyz23456789", 10).Insert(5, "-"))
            .ToList();
        return (codes, string.Join(';', codes.Select(HashRecoveryCode)));
    }

    /// <summary>
    /// If the recovery code is one of the stored ones, returns the stored codes without it (it can't be used again);
    /// otherwise null.
    /// </summary>
    public static string? UseRecoveryCode(string? stored, string? code)
    {
        code = Clean(code)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(code))
            return null;
        if (code.Length == 10)
            code = code.Insert(5, "-");
        var hashes = stored.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        var hash = HashRecoveryCode(code);
        var index = hashes.FindIndex(h => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(h), Encoding.ASCII.GetBytes(hash)));
        if (index < 0)
            return null;
        hashes.RemoveAt(index);
        return string.Join(';', hashes);
    }

    public static int RecoveryCodesLeft(string? stored) =>
        stored?.Split(';', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;

    private static string HashRecoveryCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static string? Clean(string? code) => code?.Replace(" ", "").Replace("\t", "").Trim();

    public static string ToBase32(byte[] data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            text.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text.ToUpperInvariant())
        {
            var value = Base32Alphabet.IndexOf(c);
            if (value < 0)
                continue; // spaces, dashes and '=' padding
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }
        return [.. bytes];
    }
}
