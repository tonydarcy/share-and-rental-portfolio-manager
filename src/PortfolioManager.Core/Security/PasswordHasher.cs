using System.Security.Cryptography;
using System.Text;

namespace PortfolioManager.Core.Security;

/// <summary>
/// Password hashing for tblUsers.strLoginPassword, in the format the old Web Forms site used,
/// so existing logins keep working.
/// Stored format: PBKDF2$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt; (HMAC-SHA1, UTF-8 password).
/// Anything not in that format is a legacy plain text password, accepted once so it can be upgraded.
/// </summary>
public static class PasswordHasher
{
    private const string Prefix = "PBKDF2";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    // SHA1 because .NET 4.0 (the legacy site) has no other PBKDF2 variant.
    // The iteration count is stored with each hash, so it can be raised later.
    public const int Iterations = 310000;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return Format(Iterations, salt, Derive(password, salt, Iterations, HashBytes));
    }

    public static bool IsHashed(string? stored) =>
        stored is not null && stored.StartsWith(Prefix + "$", StringComparison.Ordinal);

    /// <summary>
    /// Checks a password against the stored value. needsUpgrade is true when it matched
    /// but the stored value is plain text or uses fewer iterations than today.
    /// </summary>
    public static bool VerifyPassword(string? password, string? stored, out bool needsUpgrade)
    {
        needsUpgrade = false;
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored))
            return false;

        if (!IsHashed(stored))
        {
            var matches = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(stored));
            needsUpgrade = matches;
            return matches;
        }

        var parts = stored.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations) || iterations <= 0)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Derive(password, salt, iterations, expected.Length);
        var valid = CryptographicOperations.FixedTimeEquals(actual, expected);
        needsUpgrade = valid && iterations < Iterations;
        return valid;
    }

    internal static string Format(int iterations, byte[] salt, byte[] hash) =>
        string.Join("$", Prefix, iterations.ToString(), Convert.ToBase64String(salt), Convert.ToBase64String(hash));

    private static byte[] Derive(string password, byte[] salt, int iterations, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA1, length);
}
