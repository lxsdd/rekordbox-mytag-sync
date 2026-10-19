using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RekordboxMyTagSync.Core;

/// <summary>
/// Read-only Windows resolution of an existing encrypted master.db.
/// SUBST, junction and share aliases must not create extra physical libraries.
/// Multiple hardlinks are rejected because restoring one via File.Replace
/// would leave the other link pointing at the old encrypted database.
/// </summary>
internal static class PhysicalDatabasePath
{
    internal static string ResolveExisting(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Physical DB identity requires Windows.");
        var source = Path.GetFullPath(path);
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1);
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            throw new IOException("Could not read native database file identity.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        if (info.NumberOfLinks != 1)
            throw new InvalidDataException(
                "master.db has multiple hardlinks or unverified link count; " +
                "atomic replace/restore cannot safely identify a unique target.");

        var capacity = 512;
        for (var i = 0; i < 3; i++)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(stream.SafeFileHandle,
                buffer, (uint)capacity, 0);
            if (length == 0)
                throw new IOException("Unable to resolve final Windows database path.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            if (length >= capacity)
            {
                if (length > 32760)
                    throw new InvalidDataException("Physical database path is too long.");
                capacity = checked((int)length + 1);
                continue;
            }
            var value = buffer.ToString();
            if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                value = @"\\" + value[8..];
            else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                value = value[4..];
            else
                throw new InvalidDataException("Unsupported native database path type.");
            if (!Path.IsPathFullyQualified(value) ||
                (!value.StartsWith(@"\\", StringComparison.Ordinal) &&
                 (value.Length < 3 || value[1] != ':' || value[2] != '\\')))
                throw new InvalidDataException(
                    "Database final path is not a stable DOS or UNC name.");
            return Path.GetFullPath(value).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        throw new InvalidDataException("Physical database path could not be resolved.");
    }

    internal static string ForDiscovery(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Nonexisting candidate directories/files may be displayed as unsafe.
        return File.Exists(full) ? ResolveExisting(full) : full;
    }

    internal static string SlotIdentity(string path) =>
        ResolveExisting(path).ToUpperInvariant();

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoNative
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh;
        public uint WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks;
        public uint IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true,
        EntryPoint = "GetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file, out FileInfoNative info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode,
        ExactSpelling = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file, StringBuilder buffer, uint capacity, uint flags);
}
