using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RekordboxMyTagSync.Core;

internal static class BackupRestoreSelfTest
{
    public static void Run(string tempRoot)
    {
        var root = Path.Combine(tempRoot, "backup-restore");
        var dbDir = Path.Combine(root, "database");
        var backupRoot = Path.Combine(root, "backups");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "master.db");
        var bytesA = Encoding.UTF8.GetBytes("synthetic-sqlcipher-fixture-A");
        var bytesB = Encoding.UTF8.GetBytes("synthetic-sqlcipher-fixture-B-with-drift");
        var bytesC = Encoding.UTF8.GetBytes("synthetic-sqlcipher-fixture-C-new-prewrite-state");
        File.WriteAllBytes(dbPath, bytesA);

        var identityA = Identity(dbPath, "DB-ROLLING-1", "7.0.0");
        var mappingHash = Hash("mapping-v1");
        var first = RekordboxRollingBackup.CreateOrReplace(backupRoot, identityA, mappingHash, "0.1.0-dev");
        if (!File.Exists(first.PackagePath)) throw new InvalidOperationException("rolling backup package was not created");
        if (Directory.EnumerateFiles(backupRoot, "*.rbbackup").Count() != 1)
            throw new InvalidOperationException("rolling backup created more than one package for one database");
        if (!string.Equals(first.Metadata.DbId, identityA.DbId, StringComparison.Ordinal) ||
            !string.Equals(first.Metadata.DbVersion, identityA.DbVersion, StringComparison.Ordinal) ||
            !string.Equals(first.Metadata.MappingHashSha256, mappingHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(first.Metadata.DatabaseSha256, Hash(bytesA), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("rolling backup metadata mismatch");

        File.WriteAllBytes(dbPath, bytesB);
        var identityB = Identity(dbPath, identityA.DbId, identityA.DbVersion);
        _ = RekordboxRollingBackup.Restore(first.PackagePath, identityB);
        if (!File.ReadAllBytes(dbPath).SequenceEqual(bytesA))
            throw new InvalidOperationException("restore did not atomically restore the validated database payload");

        var afterRestoreIdentity = Identity(dbPath, identityA.DbId, identityA.DbVersion);
        var packagePath = RekordboxRollingBackup.CreateOrReplace(backupRoot, afterRestoreIdentity, Hash("mapping-v2"), "0.1.0-dev").PackagePath;
        File.WriteAllBytes(dbPath, bytesC);
        var identityC = Identity(dbPath, identityA.DbId, identityA.DbVersion);
        var replaced = RekordboxRollingBackup.CreateOrReplace(backupRoot, identityC, Hash("mapping-v3"), "0.1.0-dev");
        if (!string.Equals(packagePath, replaced.PackagePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("rolling backup slot changed for the same database identity/path");
        if (Directory.EnumerateFiles(backupRoot, "*.rbbackup").Count() != 1)
            throw new InvalidOperationException("rolling backup replacement retained history");
        if (!string.Equals(replaced.Metadata.DatabaseSha256, Hash(bytesC), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("rolling backup was not replaced with the latest pre-write image");

        // A stale approved snapshot must not replace the last known-good rolling
        // package, even when the changed database has exactly the same byte length.
        var packageBeforeRejectedBackup = Hash(File.ReadAllBytes(replaced.PackagePath));
        var changedSameLength = bytesC.ToArray();
        changedSameLength[0] ^= 0x01;
        File.WriteAllBytes(dbPath, changedSameLength);
        File.SetLastWriteTimeUtc(dbPath, identityC.LastWriteUtc.AddSeconds(5));
        AssertInvalid(
            () => RekordboxRollingBackup.CreateOrReplace(
                backupRoot, identityC, Hash("stale-mapping"), "0.1.0-dev"),
            "stale approved database identity did not block rolling replacement");
        if (!string.Equals(Hash(File.ReadAllBytes(replaced.PackagePath)), packageBeforeRejectedBackup, StringComparison.Ordinal))
            throw new InvalidOperationException("rejected backup attempt damaged the previous rolling package");
        File.WriteAllBytes(dbPath, bytesC);

        // A damaged archive must fail inspection and restore before changing
        // the target database, even if its metadata still looks valid.
        var tampered = Path.Combine(root, "tampered.rbbackup");
        File.Copy(replaced.PackagePath, tampered);
        using (var stream = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
        {
            archive.GetEntry("master.db")!.Delete();
            using var damagedEntry = archive.CreateEntry("master.db", CompressionLevel.NoCompression).Open();
            damagedEntry.Write(bytesA);
        }
        var targetBeforeRejectedRestore = Hash(File.ReadAllBytes(dbPath));
        AssertInvalid(() => RekordboxRollingBackup.Inspect(tampered, identityC),
            "modified archive bytes were not rejected by SHA-256 verification");
        AssertInvalid(() => RekordboxRollingBackup.Restore(tampered, identityC),
            "restore accepted an archive whose database bytes do not match metadata");
        if (!string.Equals(Hash(File.ReadAllBytes(dbPath)), targetBeforeRejectedRestore, StringComparison.Ordinal))
            throw new InvalidOperationException("rejected restore modified the target database");

        var wrongDbId = identityC with { DbId = "DB-OTHER" };
        AssertInvalid(() => RekordboxRollingBackup.Inspect(replaced.PackagePath, wrongDbId), "backup DBID mismatch did not fail closed");

        var malformed = Path.Combine(root, "malformed.rbbackup");
        File.Copy(replaced.PackagePath, malformed);
        using (var stream = new FileStream(malformed, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
        {
            using var extra = archive.CreateEntry("unexpected.txt").Open();
            extra.WriteByte(1);
        }
        AssertInvalid(() => RekordboxRollingBackup.Inspect(malformed, identityC), "backup with unexpected package entries did not fail closed");
    }

    private static RekordboxDatabaseIdentity Identity(string path, string dbId, string dbVersion)
    {
        var file = new FileInfo(path);
        return new RekordboxDatabaseIdentity(
            dbId,
            dbVersion,
            Path.GetFullPath(path),
            file.Length,
            file.LastWriteTimeUtc,
            "synthetic");
    }

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AssertInvalid(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
