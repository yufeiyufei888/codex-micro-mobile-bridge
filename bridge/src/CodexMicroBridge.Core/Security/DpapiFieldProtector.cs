using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexMicroBridge.Core.Security;

public interface IFieldProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedOrPlaintext);
}

public sealed partial class DpapiFieldProtector : IFieldProtector
{
    private const string Prefix = "dpapi:v1:";
    private const uint CryptProtectUiForbidden = 0x1;
    private readonly byte[] _entropy;

    public DpapiFieldProtector(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes(purpose));
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var ciphertext = ProtectCurrentUser(Encoding.UTF8.GetBytes(plaintext), _entropy);
        return Prefix + Convert.ToBase64String(ciphertext);
    }

    public string Unprotect(string protectedOrPlaintext)
    {
        ArgumentNullException.ThrowIfNull(protectedOrPlaintext);
        if (!protectedOrPlaintext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // Compatibility with installations created before field encryption.
            return protectedOrPlaintext;
        }

        var ciphertext = Convert.FromBase64String(protectedOrPlaintext[Prefix.Length..]);
        var plaintext = UnprotectCurrentUser(ciphertext, _entropy);
        return Encoding.UTF8.GetString(plaintext);
    }

    internal static byte[] ProtectCurrentUser(byte[] plaintext, byte[] entropy)
    {
        using var input = DataBlobHandle.FromBytes(plaintext);
        using var optionalEntropy = DataBlobHandle.FromBytes(entropy);
        if (!CryptProtectData(
                ref input.Blob,
                null,
                ref optionalEntropy.Blob,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI encryption failed.");
        }

        try
        {
            return CopyBlob(output);
        }
        finally
        {
            _ = LocalFree(output.Data);
        }
    }

    internal static byte[] UnprotectCurrentUser(byte[] ciphertext, byte[] entropy)
    {
        using var input = DataBlobHandle.FromBytes(ciphertext);
        using var optionalEntropy = DataBlobHandle.FromBytes(entropy);
        if (!CryptUnprotectData(
                ref input.Blob,
                out var description,
                ref optionalEntropy.Blob,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI decryption failed.");
        }

        try
        {
            return CopyBlob(output);
        }
        finally
        {
            if (description != IntPtr.Zero)
            {
                _ = LocalFree(description);
            }
            _ = LocalFree(output.Data);
        }
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        var bytes = new byte[blob.Length];
        if (bytes.Length > 0)
        {
            Marshal.Copy(blob.Data, bytes, 0, bytes.Length);
        }
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    private sealed class DataBlobHandle : IDisposable
    {
        private DataBlobHandle(DataBlob blob)
        {
            Blob = blob;
        }

        public DataBlob Blob;

        public static DataBlobHandle FromBytes(byte[] bytes)
        {
            var pointer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }
            return new DataBlobHandle(new DataBlob { Length = bytes.Length, Data = pointer });
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Blob.Data);
            Blob = default;
        }
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DataBlob input,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DataBlob input,
        out IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}

public sealed class PassthroughFieldProtector : IFieldProtector
{
    public string Protect(string plaintext) => plaintext;

    public string Unprotect(string protectedOrPlaintext) => protectedOrPlaintext;
}
