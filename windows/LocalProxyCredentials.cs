using System.Security.Cryptography;

namespace Anarise;

internal static class LocalProxyCredentials
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static string Create(int length = 18)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return string.Create(length, bytes, (chars, random) =>
        {
            for (int i = 0; i < chars.Length; i++) chars[i] = Alphabet[random[i] % Alphabet.Length];
        });
    }
}
