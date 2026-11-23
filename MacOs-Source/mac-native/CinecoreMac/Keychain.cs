using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CinecorePlayer2025.Utilities;

internal static class Keychain
{
    private static readonly KeychainSession Session = new(new MacKeychainBackend());
    public static bool AccessRequired => Session.AccessRequired;
    public static bool Authorizing => Session.Authorizing;
    public static string? Get(string account) => Environment.GetEnvironmentVariable("CINECORE_QA_NO_ACCOUNTS") == "1" ? null : Session.Get(account);
    public static bool Set(string account, string value) => Session.Set(account, value);
    public static void Delete(string account) => Session.Delete(account);
    public static bool AuthorizePending() => Session.AuthorizePending();
}

internal sealed class MacKeychainBackend : IKeychainBackend
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("Cinecore Player");
    [DllImport(Security)] private static extern int SecKeychainGetUserInteractionAllowed(out byte allowed);
    [DllImport(Security)] private static extern int SecKeychainSetUserInteractionAllowed(byte allowed);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint passwordLength, byte[] password, IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint passwordLength, out IntPtr password, out IntPtr item);
    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int FindReference(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, IntPtr passwordLength, IntPtr password, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport(Security)] private static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    // Tutte le operazioni passano dalla sessione serializzata. Ripristinare SEMPRE lo stato precedente,
    // anche in caso di annullamento: nessuna modifica permanente alle preferenze del Portachiavi.
    private static KeychainRead InMode(bool interactive, Func<KeychainRead> operation)
    {
        int status = SecKeychainGetUserInteractionAllowed(out byte previous);
        if (status != 0) return new(status, null);
        status = SecKeychainSetUserInteractionAllowed(interactive ? (byte)1 : (byte)0);
        if (status != 0) return new(status, null);
        try { return operation(); }
        finally { SecKeychainSetUserInteractionAllowed(previous); }
    }

    public KeychainRead Read(string account, bool interactive) => InMode(interactive, () =>
    {
        byte[] a = Encoding.UTF8.GetBytes(account);
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)a.Length, a, out uint length, out IntPtr data, out IntPtr item);
        try { return new(status, status == 0 ? Marshal.PtrToStringUTF8(data, checked((int)length)) : null); }
        finally { if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data); if (item != IntPtr.Zero) CFRelease(item); }
    });

    public int Write(string account, string value) => InMode(false, () =>
    {
        byte[] a = Encoding.UTF8.GetBytes(account), password = Encoding.UTF8.GetBytes(value);
        try
        {
            // Cercare solo il riferimento evita una lettura protetta superflua prima di scrivere.
            int status = FindReference(IntPtr.Zero, (uint)Service.Length, Service, (uint)a.Length, a, IntPtr.Zero, IntPtr.Zero, out IntPtr item);
            try
            {
                if (status == 0) return new(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password), null);
                if (status != -25300) return new(status, null);
                return new(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)a.Length, a, (uint)password.Length, password, IntPtr.Zero), null);
            }
            finally { if (item != IntPtr.Zero) CFRelease(item); }
        }
        finally { CryptographicOperations.ZeroMemory(password); }
    }).Status;

    public int Delete(string account) => InMode(false, () =>
    {
        byte[] a = Encoding.UTF8.GetBytes(account);
        int status = FindReference(IntPtr.Zero, (uint)Service.Length, Service, (uint)a.Length, a, IntPtr.Zero, IntPtr.Zero, out IntPtr item);
        try { return new(status == 0 ? SecKeychainItemDelete(item) : status, null); }
        finally { if (item != IntPtr.Zero) CFRelease(item); }
    }).Status;
}
