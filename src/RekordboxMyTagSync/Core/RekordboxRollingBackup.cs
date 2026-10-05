using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RekordboxMyTagSync.Core;

public sealed record RekordboxBackupMetadata(
    int SchemaVersion,
    string DbId,
    string DbVersion,
    string CanonicalDatabasePath,
    string DatabaseSha256,
    string ToolVersion,
    string MappingHashSha256,
    long DatabaseLength,
    DateTime CreatedUtc);

public sealed record RekordboxBackupInspection(
    string PackagePath,
    RekordboxBackupMetadata Metadata);

public static class RekordboxRollingBackup
{
    public const int CurrentSchemaVersion = 1;
    private const string DatabaseEntryName = "master.db";
    private const string MetadataEntryName = "metadata.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string GetPackagePath(string backupRoot, RekordboxDatabaseIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        ArgumentNullException.ThrowIfNull(identity);
        ValidateIdentity(identity);
        var canonicalRoot = Path.GetFullPath(backupRoot);
        var keyMaterial = identity.DbId.Trim() + "\n" + Canonical(identity.CanonicalPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial))).ToLowerInvariant();
        return Path.Combine(canonicalRoot, key + ".rbbackup");
    }

    public static RekordboxBackupInspection CreateOrReplace(
        string backupRoot,
        RekordboxDatabaseIdentity identity,
        string mappingHashSha256,
        string toolVersion)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingHashSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        ValidateIdentity(identity);
        ValidateSha256(mappingHashSha256, nameof(mappingHashSha256));
        EnsureRekordboxClosed();

        var sourcePath = Canonical(identity.CanonicalPath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("rekordbox master.db not found.", sourcePath);
        var sourceInfo = new FileInfo(sourcePath);
        if (sourceInfo.Length != identity.FileLength || sourceInfo.LastWriteTimeUtc != identity.LastWriteUtc)
            throw new InvalidOperationException("rekordbox database changed after the approved database snapshot; backup creation is blocked.");

        var packagePath = GetPackagePath(backupRoot, identity);
        var directory = Path.GetDirectoryName(packagePath)!;
        Directory.CreateDirectory(directory);
        var tempPath = packagePath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            var databaseHash = ComputeFileSha256(sourcePath);
            var metadata = new RekordboxBackupMetadata(
                CurrentSchemaVersion,
                identity.DbId.Trim(),
                identity.DbVersion.Trim(),
                sourcePath,
                databaseHash,
                toolVersion.Trim(),
                mappingHashSha256.ToLowerInvariant(),
                sourceInfo.Length,
                DateTime.UtcNow);

            using (var packageStream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var dbEntry = archive.CreateEntry(DatabaseEntryName, CompressionLevel.NoCompression);
                using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                using (var output = dbEntry.Open())
                    input.CopyTo(output);

                var metadataEntry = archive.CreateEntry(MetadataEntryName, CompressionLevel.Optimal);
                using var metadataStream = metadataEntry.Open();
                JsonSerializer.Serialize(metadataStream, metadata, JsonOptions);
            }

            var inspection = Inspect(tempPath, identity);
            if (!string.Equals(inspection.Metadata.MappingHashSha256, mappingHashSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup mapping hash verification failed.");

            if (File.Exists(packagePath))
                File.Replace(tempPath, packagePath, destinationBackupFileName: null, ignoreMetadataErrors: false);
            else
                File.Move(tempPath, packagePath);

            return Inspect(packagePath, identity);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public static RekordboxBackupInspection Inspect(string packagePath, RekordboxDatabaseIdentity expectedIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ValidateIdentity(expectedIdentity);
        var canonicalPackagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(canonicalPackagePath)) throw new FileNotFoundException("Rolling rekordbox backup not found.", canonicalPackagePath);

        using var stream = new FileStream(canonicalPackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var dbEntries = archive.Entries.Where(x => string.Equals(x.FullName, DatabaseEntryName, StringComparison.Ordinal)).ToArray();
        var metadataEntries = archive.Entries.Where(x => string.Equals(x.FullName, MetadataEntryName, StringComparison.Ordinal)).ToArray();
        if (archive.Entries.Count != 2 || dbEntries.Length != 1 || metadataEntries.Length != 1)
            throw new InvalidDataException("Backup package must contain exactly master.db and metadata.json.");

        RekordboxBackupMetadata? metadata;
        try
        {
            using var metadataStream = metadataEntries[0].Open();
            metadata = JsonSerializer.Deserialize<RekordboxBackupMetadata>(metadataStream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Backup metadata is invalid JSON.", ex);
        }
        if (metadata is null) throw new InvalidDataException("Backup metadata is empty.");
        ValidateMetadata(metadata, expectedIdentity);

        using var dbStream = dbEntries[0].Open();
        var backupHash = ComputeSha256(dbStream);
        if (!string.Equals(backupHash, metadata.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup database SHA-256 does not match metadata.");
        if (dbEntries[0].Length != metadata.DatabaseLength)
            throw new InvalidDataException("Backup database length does not match metadata.");

        return new RekordboxBackupInspection(canonicalPackagePath, metadata);
    }

    public static RekordboxBackupInspection Restore(
        string packagePath,
        RekordboxDatabaseIdentity currentIdentity)
    {
        ArgumentNullException.ThrowIfNull(currentIdentity);
        ValidateIdentity(currentIdentity);
        EnsureRekordboxClosed();
        var inspection = Inspect(packagePath, currentIdentity);
        var targetPath = Canonical(currentIdentity.CanonicalPath);
        if (!File.Exists(targetPath))
            throw new FileNotFoundException("Restore target master.db does not exist; restore is blocked fail-closed.", targetPath);

        var targetDirectory = Path.GetDirectoryName(targetPath)!;
        var tempPath = Path.Combine(targetDirectory, Path.GetFileName(targetPath) + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var packageStream = new FileStream(inspection.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false))
            using (var input = archive.GetEntry(DatabaseEntryName)!.Open())
            using (var output = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            var restoredHash = ComputeFileSha256(tempPath);
            if (!string.Equals(restoredHash, inspection.Metadata.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Restored database SHA-256 does not match the validated backup package.");

            File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: false);
            if (!string.Equals(ComputeFileSha256(targetPath), inspection.Metadata.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Post-restore database SHA-256 verification failed.");

            return Inspect(inspection.PackagePath, inspection.Metadata.ToIdentity(currentIdentity.SqliteCipherVersion));
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static void EnsureRekordboxClosed()
    {
        if (RekordboxProcessGuard.IsRunning())
            throw new InvalidOperationException("rekordbox is running. Backup/restore is blocked until rekordbox is closed.");
    }

    private static void ValidateMetadata(RekordboxBackupMetadata metadata, RekordboxDatabaseIdentity identity)
    {
        if (metadata.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported backup schema version '{metadata.SchemaVersion}'.");
        if (!string.Equals(metadata.DbId?.Trim(), identity.DbId.Trim(), StringComparison.Ordinal))
            throw new InvalidDataException("Backup DBID does not match the selected rekordbox database.");
        if (!string.Equals(metadata.DbVersion?.Trim(), identity.DbVersion.Trim(), StringComparison.Ordinal))
            throw new InvalidDataException("Backup DBVersion does not match the selected rekordbox database.");
        if (!string.Equals(Canonical(metadata.CanonicalDatabasePath), Canonical(identity.CanonicalPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup database path does not match the selected rekordbox database.");
        ValidateSha256(metadata.DatabaseSha256, nameof(metadata.DatabaseSha256));
        ValidateSha256(metadata.MappingHashSha256, nameof(metadata.MappingHashSha256));
        if (string.IsNullOrWhiteSpace(metadata.ToolVersion)) throw new InvalidDataException("Backup tool version is empty.");
        if (metadata.DatabaseLength < 0) throw new InvalidDataException("Backup database length is invalid.");
    }

    private static void ValidateIdentity(RekordboxDatabaseIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.DbId)) throw new ArgumentException("Database DBID is empty.", nameof(identity));
        if (string.IsNullOrWhiteSpace(identity.DbVersion)) throw new ArgumentException("Database DBVersion is empty.", nameof(identity));
        if (string.IsNullOrWhiteSpace(identity.CanonicalPath)) throw new ArgumentException("Database path is empty.", nameof(identity));
        if (identity.FileLength < 0) throw new ArgumentException("Database file length is invalid.", nameof(identity));
    }

    private static void ValidateSha256(string value, string name)
    {
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Expected a 64-character SHA-256 hex string.", name);
    }

    private static string Canonical(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return ComputeSha256(stream);
    }

    private static string ComputeSha256(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static RekordboxDatabaseIdentity ToIdentity(this RekordboxBackupMetadata metadata, string cipherVersion) =>
        new(metadata.DbId, metadata.DbVersion, Canonical(metadata.CanonicalDatabasePath), metadata.DatabaseLength, DateTime.MinValue, cipherVersion);
}
