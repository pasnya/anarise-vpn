using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace Anarise;

internal static class SecureStorage
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ANARISE-VPN-profile-v1");

    public static string Protect(string value)
    {
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Unprotect(string value)
    {
        var decrypted = ProtectedData.Unprotect(
            Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
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
        var temporaryPath = path + ".part";
        File.WriteAllText(temporaryPath, Protect(content), Encoding.UTF8);
        File.Move(temporaryPath, path, true);
    }
}
