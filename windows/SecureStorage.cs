using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Anarise;

internal static class SecureStorage
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ANARISE-VPN-profile-v1");
    private const string AesEnvelopePrefix = "AESGCM1:";

    public static string Protect(string value)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
#endif

        byte[] key = GetCrossPlatformKey();
        byte[] plaintextBytes = Encoding.UTF8.GetBytes(value);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] ciphertext = new byte[plaintextBytes.Length];
        byte[] tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, Entropy);

        byte[] combined = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length + tag.Length, ciphertext.Length);

        return AesEnvelopePrefix + Convert.ToBase64String(combined);
    }

    public static string Unprotect(string value)
    {
        if (value.StartsWith(AesEnvelopePrefix, StringComparison.Ordinal))
        {
            byte[] combined = Convert.FromBase64String(value.Substring(AesEnvelopePrefix.Length));
            if (combined.Length < 28)
                throw new CryptographicException("Invalid encrypted envelope.");

            byte[] nonce = new byte[12];
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[combined.Length - 28];

            Buffer.BlockCopy(combined, 0, nonce, 0, 12);
            Buffer.BlockCopy(combined, 12, tag, 0, 16);
            Buffer.BlockCopy(combined, 28, ciphertext, 0, ciphertext.Length);

            byte[] key = GetCrossPlatformKey();
            byte[] plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Entropy);
            return Encoding.UTF8.GetString(plaintext);
        }

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
#endif

        throw new CryptographicException("Unsupported or unencrypted storage format.");
    }

    public static bool TryRead(string path, out string content)
    {
        content = string.Empty;
        try
        {
            if (!File.Exists(path)) return false;
            content = Unprotect(File.ReadAllText(path));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Write(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temporaryPath = path + ".part";
        File.WriteAllText(temporaryPath, Protect(content), Encoding.UTF8);
        File.Move(temporaryPath, path, true);
    }

    private static byte[] GetCrossPlatformKey()
    {
        string appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".anarise");
        Directory.CreateDirectory(appDir);
        string keyFile = Path.Combine(appDir, ".keystore");

        try
        {
            if (File.Exists(keyFile))
            {
                byte[] stored = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
                if (stored.Length == 32)
                    return stored;
            }
        }
        catch { }

        byte[] newKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            File.WriteAllText(keyFile, Convert.ToBase64String(newKey), Encoding.UTF8);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch { }

        return newKey;
    }
}
