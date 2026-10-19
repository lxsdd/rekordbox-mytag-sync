using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RekordboxMyTagSync.Core;

public static class PhysicalDatabaseAliasSelfTest
{
    public static void Run(string temp)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(temp, "physical-db-alias");
        var actualDirectory = Path.Combine(root, "real");
        var junction = Path.Combine(root, "junction");
        Directory.CreateDirectory(actualDirectory);
        var original = Path.Combine(actualDirectory, "master.db");
        EncryptedMutationFixture.Create(original,
            Path.Combine(root, "audio", "a.mp3"),
            Path.Combine(root, "audio", "b.mp3"));
        CreateJunction(junction, actualDirectory);
        var alias = Path.Combine(junction, "master.db");
        var policy = new RekordboxDatabaseReadPolicy(
            new HashSet<string>(StringComparer.Ordinal) {
                EncryptedMutationFixture.DbVersion }, RequireRekordboxClosed: false);
        var first = RekordboxSqlCipherDatabase.ReadSnapshot(
            original, EncryptedMutationFixture.Key, policy);
        var second = RekordboxSqlCipherDatabase.ReadSnapshot(
            alias, EncryptedMutationFixture.Key, policy);
        if (first.Identity.PreviewIdentity != second.Identity.PreviewIdentity ||
            !RekordboxDiscovery.PathsEqual(original, alias))
            throw new InvalidOperationException("SQLCipher alias was misidentified as a separate library.");

        var pioneer = Path.Combine(root, "Pioneer");
        var settings = Path.Combine(pioneer, "rekordbox6", "rekordbox3.settings");
        var agent = Path.Combine(pioneer, "rekordboxAgent", "storage", "options.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        Directory.CreateDirectory(Path.GetDirectoryName(agent)!);
        new XDocument(new XElement("ROOT", new XElement("VALUE",
            new XAttribute("name", "masterDbDirectory"),
            new XAttribute("val", junction)))).Save(settings);
        File.WriteAllText(agent, JsonSerializer.Serialize(new {
            options = new object[][] { new object[] { "db-path", original } }
        }), new UTF8Encoding(false));
        var discovered = RekordboxDiscovery.Discover(
            new RekordboxDiscoveryOptions(Path.Combine(root, "empty-install-root"), pioneer));
        if (discovered.Libraries.Count != 1 ||
            discovered.Libraries[0].Evidence.Count != 2 ||
            discovered.Diagnostics.Any(x => x.Contains("database-path conflict", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "Two rekordbox settings aliases did not converge on one physical master.db.");

        var backupDir = Path.Combine(root, "backups");
        var stateDir = Path.Combine(root, "provenance");
        var packageA = RekordboxRollingBackup.GetPackagePath(backupDir, first.Identity);
        var packageB = RekordboxRollingBackup.GetPackagePath(backupDir, second.Identity);
        var stateA = ProvenanceStore.GetStatePath(stateDir, first.Identity);
        var stateB = ProvenanceStore.GetStatePath(stateDir, second.Identity);
        if (!string.Equals(packageA, packageB, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(stateA, stateB, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Two aliases to one master.db created separate backup/provenance slots.");

        var initialHash = RekordboxRollingBackup.ComputeFileSha256(original);
        var created = RekordboxRollingBackup.CreateOrReplace(
            backupDir, first.Identity, Hash("mapping-A"), "0.1.0-dev",
            expectedDatabaseSha256: initialHash);
        var empty = ProvenanceStore.Empty(first.Identity);
        ProvenanceStore.SaveAtomic(stateA, empty, first.Identity);
        var reopened = ProvenanceStore.Load(stateB, second.Identity);
        if (reopened.DbId != empty.DbId ||
            reopened.CanonicalDatabasePath != empty.CanonicalDatabasePath ||
            reopened.Assignments.Count != 0)
            throw new InvalidOperationException("Aliased provenance did not round-trip.");
        var updated = RekordboxRollingBackup.CreateOrReplace(
            backupDir, second.Identity, Hash("mapping-B"), "0.1.0-dev",
            expectedDatabaseSha256: initialHash);
        if (created.PackagePath != updated.PackagePath ||
            Directory.GetFiles(backupDir, "*.rbbackup").Length != 1 ||
            RekordboxRollingBackup.Inspect(updated.PackagePath, first.Identity)
                .Metadata.MappingHashSha256 != Hash("mapping-B") ||
            RekordboxRollingBackup.ComputeFileSha256(original) != initialHash)
            throw new InvalidOperationException(
                "Rolling backup was duplicated or synthetic SQLCipher original changed.");

        // Matching DBID/DBVersion does not make a copied DB the same physical file.
        var otherPath = Path.Combine(root, "copy", "master.db");
        Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
        File.Copy(original, otherPath);
        var copied = RekordboxSqlCipherDatabase.ReadSnapshot(
            otherPath, EncryptedMutationFixture.Key, policy);
        if (copied.Identity.DbId != first.Identity.DbId ||
            RekordboxDiscovery.PathsEqual(otherPath, original) ||
            RekordboxRollingBackup.GetPackagePath(backupDir, copied.Identity) == packageA ||
            ProvenanceStore.GetStatePath(stateDir, copied.Identity) == stateA)
            throw new InvalidOperationException(
                "Independent copied master.db was silently deduplicated by DBID.");

        var hardlink = Path.Combine(actualDirectory, "hardlinked.db");
        if (!CreateHardLink(hardlink, original, IntPtr.Zero))
            throw new IOException("Hardlink synthetic fixture setup failed: " +
                Marshal.GetLastWin32Error());
        try
        {
            var blocked = false;
            try { _ = PhysicalDatabasePath.ResolveExisting(hardlink); }
            catch (InvalidDataException) { blocked = true; }
            if (!blocked)
                throw new InvalidOperationException(
                    "Unsafe hardlinked master.db was accepted as uniquely restorable.");
        }
        finally { File.Delete(hardlink); }

        if (RekordboxRollingBackup.ComputeFileSha256(original) != initialHash)
            throw new InvalidOperationException(
                "Alias E2E changed synthetic encrypted database bytes.");
    }

    private static void CreateJunction(string link, string target)
    {
        var command = new ProcessStartInfo("cmd.exe",
            "/d /c mklink /J \"" + link + "\" \"" + target + "\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var process = Process.Start(command)
            ?? throw new InvalidOperationException("Failed to create synthetic junction.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link))
            throw new InvalidOperationException(
                "Synthetic junction could not be created: " + output + error);
    }

    private static string Hash(string input) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    [DllImport("kernel32.dll", SetLastError = true,
        CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string newName, string existingName, IntPtr securityAttributes);
}
