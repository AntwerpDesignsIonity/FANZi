using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Fanzi.FanControl.Services;

/// <summary>
/// 4.6: protects a small secret (the SMTP password) for storage in settings.json. On Windows it uses DPAPI
/// (CryptProtectData, current-user scope): only the same Windows account on the same PC can read it back, and the
/// settings file never holds the password in clear text. Other platforms have no equivalent without a keyring
/// dependency, so <see cref="Protect"/> returns null there and the password stays in memory for the session only.
/// Never throws: a secret that cannot be read back is treated as "not set".
/// </summary>
internal static class SecretProtector
{
    internal const string Prefix = "dpapi:";

    /// <summary>True when secrets can be stored on this platform.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Returns the protected form ("dpapi:…") or null when it cannot / need not be stored.</summary>
    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText) || !OperatingSystem.IsWindows()) return null;
        try
        {
            byte[] cipher = Transform(Encoding.UTF8.GetBytes(plainText), protect: true);
            return Prefix + Convert.ToBase64String(cipher);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Protecting a secret failed — it will not be saved", ex);
            return null;
        }
    }

    /// <summary>Returns the clear text, or "" when the value is missing, foreign or from another account / PC.</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal) || !OperatingSystem.IsWindows())
            return string.Empty;
        try
        {
            byte[] plain = Transform(Convert.FromBase64String(stored[Prefix.Length..]), protect: false);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            AppLog.Warn("A saved secret could not be read back (other Windows account or PC?) — enter it again", ex);
            return string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int CryptProtectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = new DataBlob { Size = input.Length, Data = Marshal.AllocHGlobal(Math.Max(1, input.Length)) };
        var outBlob = default(DataBlob);
        try
        {
            Marshal.Copy(input, 0, inBlob.Data, input.Length);
            bool ok = protect
                ? CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

            byte[] output = new byte[outBlob.Size];
            Marshal.Copy(outBlob.Data, output, 0, outBlob.Size);
            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.Data);
            if (outBlob.Data != IntPtr.Zero) LocalFree(outBlob.Data);
        }
    }
}
