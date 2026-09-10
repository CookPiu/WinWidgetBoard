using System.Runtime.InteropServices;
using System.Text;

namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// Encrypts a small secret so it can sit in the local database without being readable from
/// the file. DPAPI in <c>CRYPTPROTECT_LOCAL_MACHINE</c>-free (current user) scope is used
/// rather than a key of our own: the protection is then tied to the Windows account that
/// owns the data directory, which is exactly the boundary the rest of the storage already
/// assumes, and there is no key of ours for the same file to leak alongside the ciphertext.
///
/// P/Invoke rather than the <c>System.Security.Cryptography.ProtectedData</c> package, so
/// this adds no dependency to a project whose versions are locked.
/// </summary>
internal static class LocalDataProtector
{
    /// <summary>Never show a UI prompt: the broker runs without a window of its own.</summary>
    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>
    /// Base64 rather than a BLOB column: the statement wrapper binds and reads text only,
    /// and a base64 string costs a third more bytes for a value this small.
    /// </summary>
    public static string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        byte[] input = Encoding.UTF8.GetBytes(plaintext);
        var inputBlob = default(DataBlob);
        var outputBlob = default(DataBlob);
        GCHandle pinned = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            inputBlob.DataSize = input.Length;
            inputBlob.DataPointer = pinned.AddrOfPinnedObject();
            if (!NativeMethods.CryptProtectData(
                    ref inputBlob,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref outputBlob))
            {
                throw new DataProtectionException(
                    "Protecting the stored credential failed.",
                    Marshal.GetLastWin32Error());
            }

            return Convert.ToBase64String(ReadBlob(outputBlob));
        }
        finally
        {
            FreeBlob(ref outputBlob);
            Array.Clear(input);
            pinned.Free();
        }
    }

    /// <summary>
    /// Returns null for anything that does not decrypt: a row copied from another machine or
    /// another account, a truncated value, or a manually edited one. A stored credential that
    /// cannot be read is treated as absent - the card then asks for it again - rather than as
    /// a database fault that would take the whole settings read down with it.
    /// </summary>
    public static string? TryUnprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
        {
            return null;
        }

        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(protectedBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        if (cipher.Length == 0)
        {
            return null;
        }

        var inputBlob = default(DataBlob);
        var outputBlob = default(DataBlob);
        GCHandle pinned = GCHandle.Alloc(cipher, GCHandleType.Pinned);
        try
        {
            inputBlob.DataSize = cipher.Length;
            inputBlob.DataPointer = pinned.AddrOfPinnedObject();
            if (!NativeMethods.CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref outputBlob))
            {
                return null;
            }

            byte[] plaintext = ReadBlob(outputBlob);
            try
            {
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                Array.Clear(plaintext);
            }
        }
        finally
        {
            FreeBlob(ref outputBlob);
            pinned.Free();
        }
    }

    private static byte[] ReadBlob(DataBlob blob)
    {
        if (blob.DataPointer == IntPtr.Zero || blob.DataSize <= 0)
        {
            return [];
        }

        byte[] buffer = new byte[blob.DataSize];
        Marshal.Copy(blob.DataPointer, buffer, 0, blob.DataSize);
        return buffer;
    }

    private static void FreeBlob(ref DataBlob blob)
    {
        if (blob.DataPointer == IntPtr.Zero)
        {
            return;
        }

        // Overwrite before releasing: the decrypted key would otherwise stay in freed heap
        // memory for as long as the allocator leaves the page alone.
        if (blob.DataSize > 0)
        {
            for (int offset = 0; offset < blob.DataSize; offset++)
            {
                Marshal.WriteByte(blob.DataPointer, offset, 0);
            }
        }

        _ = NativeMethods.LocalFree(blob.DataPointer);
        blob.DataPointer = IntPtr.Zero;
        blob.DataSize = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int DataSize;

        public IntPtr DataPointer;
    }

    private static class NativeMethods
    {
        [DllImport(
            "crypt32.dll",
            EntryPoint = "CryptProtectData",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CryptProtectData(
            ref DataBlob input,
            string? description,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            ref DataBlob output);

        [DllImport(
            "crypt32.dll",
            EntryPoint = "CryptUnprotectData",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CryptUnprotectData(
            ref DataBlob input,
            IntPtr description,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            ref DataBlob output);

        [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
        public static extern IntPtr LocalFree(IntPtr handle);
    }
}

/// <summary>
/// Thrown only when protecting a value fails, which means the credential cannot be stored at
/// all. Reading never throws - see <see cref="LocalDataProtector.TryUnprotect"/>.
/// </summary>
public sealed class DataProtectionException : InvalidOperationException
{
    public DataProtectionException(string message, int errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }
}
