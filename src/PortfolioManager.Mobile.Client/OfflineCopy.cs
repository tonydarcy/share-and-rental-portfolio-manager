using System.Security.Cryptography;
using System.Text;

namespace PortfolioManager.Mobile.Client;

/// <summary>
/// The last figures loaded from the site, kept in files on the phone so they can be shown with an "offline copy" note when
/// there's no signal. Each file is encrypted (AES-GCM) with a key the app keeps in the phone's secure storage, and the file
/// name is a hash of the address, so nothing in the folder can be read without that key. Read-only: no edits offline.
/// </summary>
public sealed class OfflineCopy(string folder, byte[] key)
{
    private const int NonceSize = 12, TagSize = 16;

    public static bool IsSupported => AesGcm.IsSupported;

    /// <summary>A new random key for <see cref="OfflineCopy"/>.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    public void Save(string address, string json) => Save(address, json, DateTimeOffset.UtcNow);

    public void Save(string address, string json, DateTimeOffset saved)
    {
        try
        {
            var plain = Encoding.UTF8.GetBytes(json);
            var file = new byte[NonceSize + TagSize + 8 + plain.Length];
            var nonce = file.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            var body = new byte[8 + plain.Length];
            BitConverter.TryWriteBytes(body, saved.ToUnixTimeMilliseconds());
            plain.CopyTo(body, 8);
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, body, file.AsSpan(NonceSize + TagSize), file.AsSpan(NonceSize, TagSize));
            Directory.CreateDirectory(folder);
            var path = PathFor(address);
            File.WriteAllBytes(path + ".tmp", file);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // A full or read-only disk only loses the offline copy, never the figures on screen
        }
    }

    /// <summary>The saved reply for an address, or null when there's none or it can't be read with this key.</summary>
    public (string Json, DateTimeOffset Saved)? Load(string address)
    {
        try
        {
            var file = File.ReadAllBytes(PathFor(address));
            if (file.Length < NonceSize + TagSize + 8)
                return null;
            var body = new byte[file.Length - NonceSize - TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(file.AsSpan(0, NonceSize), file.AsSpan(NonceSize + TagSize), file.AsSpan(NonceSize, TagSize), body);
            var saved = DateTimeOffset.FromUnixTimeMilliseconds(BitConverter.ToInt64(body, 0));
            return (Encoding.UTF8.GetString(body, 8, body.Length - 8), saved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Deletes every saved reply (on signing out).</summary>
    public static void Clear(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string PathFor(string address) =>
        Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address))) + ".bin");
}
