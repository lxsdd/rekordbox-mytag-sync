using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RekordboxMyTagSync.Core;

/// <summary>
/// Read-only, native Windows resolution of the physical master.db path.
/// A textual alias must never create a second backup/provenance authority.
/// Multiple hardlinks are rejected: replacing one link does not replace the others.
/// </summary>
internal static class RekordboxDatabaseFileIdentity
{
    internal static string ResolveCanonicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Physical rekordbox database path qualification requires Windows.");

        var requested = Path.GetFullPath(path);
        using var stream = new FileStream(requested, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var identity))
            throw new IOException("Native rekordbox database file identity is unavailable.",
                new Win32Exception(Marshal.GetLastWin32Error()));

        // Hardlink replacement through File.Replace would split one physical
        // database into two different files. Never approve such a target.
        if (identity.NumberOfLinks != 1)
            throw new InvalidDataException(
                "rekordbox master.db has multiple hardlinks; the target is ambiguous and must not be modified.");

        var required = GetFinalPathNameByHandle(stream.SafeFileHandle, null, 0, 0);
        if (required == 0 || required > 32767)
            throw new IOException("Native final database path could not be resolved.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new StringBuilder(checked((int)required + 1));
        var written = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
        if (written == 0 || written >= buffer.Capacity)
            throw new IOException("Native final database path changed during resolution.",
                new Win32Exception(Marshal.GetLastWin32Error()));

        var final = buffer.ToString();
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            final = @"\\" + final[8..];
        else if (final.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            final = final[4..];
        else
            throw new InvalidDataException("Unrecognized native database path form; target is not authorized.");

        final = Path.GetFullPath(final).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var reopened = new FileStream(final, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(reopened.SafeFileHandle, out var actual) ||
            actual.VolumeSerialNumber != identity.VolumeSerialNumber ||
            actual.FileIndexHigh != identity.FileIndexHigh ||
            actual.FileIndexLow != identity.FileIndexLow ||
            actual.NumberOfLinks != 1)
            throw new InvalidDataException(
                "Canonical database path no longer identifies the same unique physical file.");
        return final;
    }

    internal static void RequireCanonicalPath(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolved = ResolveCanonicalPath(path);
        if (!string.Equals(full, resolved, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Database path is a physical alias; select the resolved canonical master.db path before mutation.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file, out NativeFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file, StringBuilder? path, uint pathLength, uint flags);
}
