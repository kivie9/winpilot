using System.Runtime.InteropServices;
using System.Text;

namespace WinPilot.Core.Security;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

/// <summary>
/// Encrypts sensitive tokens bound to the current Windows user via DPAPI (crypt32.dll).
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi:";
    private const int CryptProtectUiForbidden = 0x1;

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[]? cipherBytes = ProtectBytes(plainBytes);
        if (cipherBytes is null)
        {
            return plaintext;
        }

        return Prefix + Convert.ToBase64String(cipherBytes);
    }

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return string.Empty;
        }

        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return protectedValue;
        }

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(protectedValue[Prefix.Length..]);
            byte[]? plainBytes = UnprotectBytes(cipherBytes);
            return plainBytes is null ? string.Empty : Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static byte[]? ProtectBytes(byte[] plainBytes)
    {
        DATA_BLOB inBlob = default;
        DATA_BLOB outBlob = default;

        try
        {
            inBlob.cbData = plainBytes.Length;
            inBlob.pbData = Marshal.AllocHGlobal(plainBytes.Length);
            Marshal.Copy(plainBytes, 0, inBlob.pbData, plainBytes.Length);

            if (!CryptProtectData(ref inBlob, "WinPilotSecret", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref outBlob))
            {
                return null;
            }

            byte[] result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    private static byte[]? UnprotectBytes(byte[] cipherBytes)
    {
        DATA_BLOB inBlob = default;
        DATA_BLOB outBlob = default;

        try
        {
            inBlob.cbData = cipherBytes.Length;
            inBlob.pbData = Marshal.AllocHGlobal(cipherBytes.Length);
            Marshal.Copy(cipherBytes, 0, inBlob.pbData, cipherBytes.Length);

            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref outBlob))
            {
                return null;
            }

            byte[] result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
