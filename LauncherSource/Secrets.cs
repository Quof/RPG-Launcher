using System.Security.Cryptography;
using System.Text;

namespace RpgLauncher;

/// <summary>
/// Encrypts saved secrets so only the same user on the same PC can read them: with Windows DPAPI on Windows,
/// elsewhere with AES-GCM and a key file only that user can open (~/.config/rpg-launcher/secret.key).
/// </summary>
internal static class Secrets
{
    private const string WindowsPrefix = "dpapi:";
    private const string KeyFilePrefix = "aes:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RpgLauncher.ProxySettings");

    public static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        byte[] plain = Encoding.UTF8.GetBytes(value);
        if (OperatingSystem.IsWindows())
        {
            byte[] data = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            return WindowsPrefix + Convert.ToBase64String(data);
        }

        byte[] key = UserKey(create: true) ?? throw new CryptographicException("No key file for saved settings");
        var sealed_ = new byte[NonceSize + TagSize + plain.Length];
        var nonce = sealed_.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, sealed_.AsSpan(NonceSize + TagSize), sealed_.AsSpan(NonceSize, TagSize), Entropy);
        return KeyFilePrefix + Convert.ToBase64String(sealed_);
    }

    /// <summary>Decrypts a saved value; text without a prefix is a plain value from an older file. Null if unreadable.</summary>
    public static string? Unprotect(string? saved)
    {
        if (string.IsNullOrEmpty(saved)) return null;
        try
        {
            if (saved.StartsWith(WindowsPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows()) throw new CryptographicException("saved on Windows");
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(saved[WindowsPrefix.Length..]), Entropy,
                    DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            if (saved.StartsWith(KeyFilePrefix, StringComparison.Ordinal))
            {
                byte[] key = UserKey(create: false) ?? throw new CryptographicException("key file missing");
                byte[] sealed_ = Convert.FromBase64String(saved[KeyFilePrefix.Length..]);
                if (sealed_.Length < NonceSize + TagSize) throw new CryptographicException("too short");
                var plain = new byte[sealed_.Length - NonceSize - TagSize];
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(sealed_.AsSpan(0, NonceSize), sealed_.AsSpan(NonceSize + TagSize),
                    sealed_.AsSpan(NonceSize, TagSize), plain, Entropy);
                return Encoding.UTF8.GetString(plain);
            }
            return saved;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            // Another user, PC or system: the setting has to be entered again.
            Diagnostics.Log("Saved proxy setting could not be decrypted: " + ex.Message);
            return null;
        }
    }

    /// <summary>The user's 256-bit key, made on first use with permissions for that user alone.</summary>
    private static byte[]? UserKey(bool create)
    {
        if (OperatingSystem.IsWindows()) return null;
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rpg-launcher");
        string path = Path.Combine(folder, "secret.key");
        if (File.Exists(path))
        {
            byte[] existing = File.ReadAllBytes(path);
            if (existing.Length == 32) return existing;
            if (!create) return null;
            File.Delete(path);
        }
        if (!create) return null;

        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        file.Write(key);
        return key;
    }
}
