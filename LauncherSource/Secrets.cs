using System.Security.Cryptography;
using System.Text;

namespace RpgLauncher;

/// <summary>Encrypts saved secrets with Windows DPAPI, so only the same Windows user on the same PC can read them.</summary>
internal static class Secrets
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RpgLauncher.ProxySettings");

    public static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(data);
    }

    /// <summary>Decrypts a saved value; text without the prefix is a plain value from an older file. Null if unreadable.</summary>
    public static string? Unprotect(string? saved)
    {
        if (string.IsNullOrEmpty(saved)) return null;
        if (!saved.StartsWith(Prefix, StringComparison.Ordinal)) return saved;
        try
        {
            byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(saved[Prefix.Length..]), Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Another Windows user or PC: the setting has to be entered again.
            Diagnostics.Log("Saved proxy setting could not be decrypted: " + ex.Message);
            return null;
        }
    }
}
