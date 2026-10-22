using PortfolioManager.Core.Security;

namespace PortfolioManager.Tests;

public class PasswordHasherTests
{
    // Generated independently with Python: hashlib.pbkdf2_hmac('sha1', 'Correct horse ✓'.encode(), bytes(range(16)), 1000, 32)
    private const string KnownHash = "PBKDF2$1000$AAECAwQFBgcICQoLDA0ODw==$yGuMJgaihKJMZ/BYcLbW07yYfN7t2M7b6eb/YJPgRHg=";

    [Fact]
    public void Verifies_a_hash_made_outside_dotnet()
    {
        Assert.True(PasswordHasher.VerifyPassword("Correct horse ✓", KnownHash, out var needsUpgrade));
        Assert.True(needsUpgrade); // 1000 iterations is below today's count
    }

    [Fact]
    public void Rejects_wrong_password()
    {
        Assert.False(PasswordHasher.VerifyPassword("correct horse ✓", KnownHash, out var needsUpgrade));
        Assert.False(needsUpgrade);
    }

    [Fact]
    public void New_hash_round_trips_in_the_legacy_format()
    {
        var stored = PasswordHasher.HashPassword("s3cret!");
        var parts = stored.Split('$');
        Assert.Equal(["PBKDF2", PasswordHasher.Iterations.ToString()], parts[..2]);
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);

        Assert.True(PasswordHasher.VerifyPassword("s3cret!", stored, out var needsUpgrade));
        Assert.False(needsUpgrade);
        Assert.NotEqual(stored, PasswordHasher.HashPassword("s3cret!")); // fresh salt each time
    }

    [Fact]
    public void Plain_text_legacy_password_matches_once_and_asks_for_upgrade()
    {
        Assert.True(PasswordHasher.VerifyPassword("old-password", "old-password", out var needsUpgrade));
        Assert.True(needsUpgrade);
        Assert.False(PasswordHasher.VerifyPassword("other", "old-password", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PBKDF2$abc$AAAA$AAAA")]
    [InlineData("PBKDF2$1000$not base64!$AAAA")]
    [InlineData("PBKDF2$1000$AAAA")]
    public void Malformed_stored_values_never_match(string? stored)
    {
        Assert.False(PasswordHasher.VerifyPassword("anything", stored, out _));
    }
}
