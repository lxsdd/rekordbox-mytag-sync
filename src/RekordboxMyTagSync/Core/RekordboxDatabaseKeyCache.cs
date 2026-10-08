using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RekordboxMyTagSync.Core;

internal static class RekordboxDatabaseKeyCache
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RBMK1");

    internal static string? TryLoad(
        string databasePath,
        string? localAppDataRoot = null)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var path = GetCachePath(databasePath, localAppDataRoot);
            if (!File.Exists(path))
                return null;

            var payload = File.ReadAllBytes(path);
            if (payload.Length <= Magic.Length ||
                !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                return null;

            var protectedBytes = payload.AsSpan(Magic.Length).ToArray();
            var entropy = Entropy(databasePath);
            try
            {
                var clear = Unprotect(protectedBytes, entropy);
                try
                {
                    var key = Encoding.UTF8.GetString(clear).Trim();
                    return key.Length == 0 ? null : key;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clear);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                CryptographicOperations.ZeroMemory(entropy);
            }
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            Win32Exception or
            CryptographicException)
        {
            return null;
        }
    }

    internal static void Save(
        string databasePath,
        string key,
        string? localAppDataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The protected rekordbox database access cache requires Windows DPAPI.");

        var clear = Encoding.UTF8.GetBytes(key.Trim());
        var entropy = Entropy(databasePath);
        try
        {
            var protectedBytes = Protect(clear, entropy);
            try
            {
                var payload = new byte[Magic.Length + protectedBytes.Length];
                Magic.CopyTo(payload, 0);
                protectedBytes.CopyTo(payload, Magic.Length);

                var path = GetCachePath(databasePath, localAppDataRoot);
                var directory = Path.GetDirectoryName(path)
                    ?? throw new InvalidDataException(
                        "Protected database access cache path has no parent directory.");
                Directory.CreateDirectory(directory);

                var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var stream = new FileStream(
                               temp,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.None,
                               4096,
                               FileOptions.WriteThrough))
                    {
                        stream.Write(payload);
                        stream.Flush(flushToDisk: true);
                    }

                    if (File.Exists(path))
                        File.Replace(temp, path, destinationBackupFileName: null);
                    else
                        File.Move(temp, path);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temp))
                            File.Delete(temp);
                    }
                    catch
                    {
                        // Never hide the primary cache result/error.
                    }
                    CryptographicOperations.ZeroMemory(payload);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    internal static string GetCachePath(
        string databasePath,
        string? localAppDataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var root = string.IsNullOrWhiteSpace(localAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataRoot;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException(
                "Local application data directory is unavailable.");

        var canonical = Canonical(databasePath);
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToUpperInvariant())))
            .ToLowerInvariant();
        return Path.Combine(
            Path.GetFullPath(root),
            "RekordboxMyTagSync",
            "database-access",
            digest + ".bin");
    }

    private static byte[] Entropy(string databasePath) =>
        SHA256.HashData(
            Encoding.UTF8.GetBytes(
                "rekordbox-mytag-sync/database-access/v1\0" +
                Canonical(databasePath).ToUpperInvariant()));

    private static string Canonical(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static byte[] Protect(byte[] clear, byte[] entropy)
    {
        var input = Allocate(clear);
        var entropyBlob = Allocate(entropy);
        try
        {
            if (!CryptProtectData(
                    ref input,
                    "Rekordbox MyTag Sync database access",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return CopyAndFreeLocal(output);
        }
        finally
        {
            FreeHGlobal(ref input);
            FreeHGlobal(ref entropyBlob);
        }
    }

    private static byte[] Unprotect(byte[] protectedBytes, byte[] entropy)
    {
        var input = Allocate(protectedBytes);
        var entropyBlob = Allocate(entropy);
        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return CopyAndFreeLocal(output);
        }
        finally
        {
            FreeHGlobal(ref input);
            FreeHGlobal(ref entropyBlob);
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        var blob = new DataBlob
        {
            Size = bytes.Length,
            Data = Marshal.AllocHGlobal(bytes.Length)
        };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }

    private static void FreeHGlobal(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
            return;

        for (var index = 0; index < blob.Size; index++)
            Marshal.WriteByte(blob.Data, index, 0);
        Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }

    private static byte[] CopyAndFreeLocal(DataBlob blob)
    {
        try
        {
            if (blob.Size <= 0 || blob.Data == IntPtr.Zero)
                throw new CryptographicException(
                    "Windows DPAPI returned an empty protected-data buffer.");

            var result = new byte[blob.Size];
            Marshal.Copy(blob.Data, result, 0, blob.Size);
            return result;
        }
        finally
        {
            if (blob.Data != IntPtr.Zero)
            {
                for (var index = 0; index < blob.Size; index++)
                    Marshal.WriteByte(blob.Data, index, 0);
                _ = LocalFree(blob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        internal int Size;
        internal IntPtr Data;
    }

    [DllImport(
        "crypt32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport(
        "crypt32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
