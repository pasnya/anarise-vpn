using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Anarise;

internal static class MacKeychainStore
{
    private const int ItemNotFound = -25300;
    private const int DuplicateItem = -25299;
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("ANARISE VPN");
    private static readonly byte[] Account = Encoding.UTF8.GetBytes("secure-storage-key-v1");

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int FindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength,
        byte[] account, out uint passwordLength, out IntPtr passwordData, out IntPtr item);

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainAddGenericPassword")]
    private static extern int AddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength,
        byte[] account, uint passwordLength, byte[] password, out IntPtr item);

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainItemFreeContent")]
    private static extern int FreeContent(IntPtr attributes, IntPtr data);

    [DllImport(CoreFoundationFramework, EntryPoint = "CFRelease")]
    private static extern void Release(IntPtr value);

    public static string? GetPassword()
    {
        int status = FindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)Account.Length, Account,
            out uint length, out IntPtr data, out IntPtr item);
        if (status == ItemNotFound) return null;
        ThrowIfFailed(status, "read the encryption key from Keychain");

        try
        {
            var password = new byte[length];
            Marshal.Copy(data, password, 0, password.Length);
            return Encoding.UTF8.GetString(password);
        }
        finally
        {
            if (data != IntPtr.Zero) FreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) Release(item);
        }
    }

    public static string AddPasswordIfAbsent(string password)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        int status = AddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)Account.Length, Account,
            (uint)passwordBytes.Length, passwordBytes, out IntPtr item);
        if (status == DuplicateItem)
        {
            if (item != IntPtr.Zero) Release(item);
            return GetPassword() ?? throw new InvalidOperationException("The Keychain item was added concurrently but could not be read.");
        }
        ThrowIfFailed(status, "save the encryption key in Keychain");
        if (item != IntPtr.Zero) Release(item);
        return password;
    }

    private static void ThrowIfFailed(int status, string operation)
    {
        if (status != 0)
            throw new InvalidOperationException($"Could not {operation} (OSStatus {status}).");
    }
}
