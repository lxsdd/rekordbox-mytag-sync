using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RekordboxMyTagSync.Core;

public sealed record OwnedAssignment(
    string ContentId,
    string MyTagId,
    string Group,
    string Value);

public sealed record ProvenanceDocument(
    int SchemaVersion,
    string DbId,
    string CanonicalDatabasePath,
    IReadOnlyList<OwnedAssignment> Assignments);

public static class ProvenanceStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static ProvenanceDocument Empty(RekordboxDatabaseIdentity identity) =>
        new(CurrentSchemaVersion, identity.DbId, Canonical(identity.CanonicalPath), Array.Empty<OwnedAssignment>());

    public static string GetStatePath(string root, RekordboxDatabaseIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.DbId))
            throw new InvalidDataException("Database DBID is empty.");

        var canonicalRoot = Path.GetFullPath(root);
        var material = identity.DbId.Trim() + "\n" + Canonical(identity.CanonicalPath);
        var fileName = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant() + ".json";
        return Path.Combine(canonicalRoot, fileName);
    }

    public static ProvenanceDocument Load(string path, RekordboxDatabaseIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(identity);
        if (!File.Exists(path)) return Empty(identity);

        ProvenanceDocument? document;
        try
        {
            using var stream = File.OpenRead(path);
            document = JsonSerializer.Deserialize<ProvenanceDocument>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Could not read provenance state.", ex);
        }

        if (document is null) throw new InvalidDataException("Provenance state is empty.");
        Validate(document, identity);
        return document with
        {
            CanonicalDatabasePath = Canonical(document.CanonicalDatabasePath),
            Assignments = document.Assignments
                .OrderBy(x => x.ContentId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.MyTagId, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    public static void SaveAtomic(string path, ProvenanceDocument document, RekordboxDatabaseIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(identity);
        Validate(document, identity);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("Provenance path has no parent directory.");
        Directory.CreateDirectory(directory);

        var normalized = document with
        {
            CanonicalDatabasePath = Canonical(document.CanonicalDatabasePath),
            Assignments = document.Assignments
                .OrderBy(x => x.ContentId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.MyTagId, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
        var temp = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
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
                JsonSerializer.Serialize(stream, normalized, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
                File.Replace(temp, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: false);
            else
                File.Move(temp, fullPath);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    public static IReadOnlyList<ManagedAssignment> ToManagedAssignments(
        ProvenanceDocument document,
        RekordboxDatabaseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(document, snapshot.Identity);

        var definitions = snapshot.MyTagDefinitions.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var parents = snapshot.MyTagDefinitions
            .Where(x => x.ParentId is null)
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var content = snapshot.Tracks.ToDictionary(x => x.ContentId, StringComparer.OrdinalIgnoreCase);
        var managed = new List<ManagedAssignment>();

        foreach (var owned in document.Assignments)
        {
            if (!content.TryGetValue(owned.ContentId, out var track))
                throw new InvalidDataException($"Provenance references missing/inactive ContentID '{owned.ContentId}'.");
            if (!definitions.TryGetValue(owned.MyTagId, out var child))
                throw new InvalidDataException($"Provenance references missing/inactive MyTagID '{owned.MyTagId}'.");
            if (child.ParentId is null || !parents.TryGetValue(child.ParentId, out var parent))
                throw new InvalidDataException($"Provenance MyTagID '{owned.MyTagId}' is not an active child MyTag.");
            if (!string.Equals(parent.Name, owned.Group, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(child.Name, owned.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Provenance name drift detected for MyTagID '{owned.MyTagId}'.");
            if (!track.Assignments.Any(x =>
                    string.Equals(x.Group, owned.Group, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Value, owned.Value, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Provenance assignment '{owned.ContentId}' → '{owned.MyTagId}' is not present in rekordbox.");

            managed.Add(new ManagedAssignment(owned.ContentId, new MyTagAssignment(parent.Name, child.Name)));
        }

        return managed;
    }

    public static ProvenanceDocument ReplaceAssignments(
        ProvenanceDocument current,
        RekordboxDatabaseIdentity identity,
        IEnumerable<OwnedAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(assignments);
        var next = new ProvenanceDocument(
            CurrentSchemaVersion,
            identity.DbId,
            Canonical(identity.CanonicalPath),
            assignments.ToArray());
        Validate(next, identity);
        return next;
    }

    private static void Validate(ProvenanceDocument document, RekordboxDatabaseIdentity identity)
    {
        if (document.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported provenance schema version '{document.SchemaVersion}'.");
        if (!string.Equals(document.DbId?.Trim(), identity.DbId, StringComparison.Ordinal))
            throw new InvalidDataException("Provenance DBID does not match the selected rekordbox database.");
        if (!string.Equals(Canonical(document.CanonicalDatabasePath), Canonical(identity.CanonicalPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Provenance database path does not match the selected rekordbox database.");
        if (document.Assignments is null) throw new InvalidDataException("Provenance assignments are missing.");

        var byLink = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in document.Assignments)
        {
            if (assignment is null || string.IsNullOrWhiteSpace(assignment.ContentId) ||
                string.IsNullOrWhiteSpace(assignment.MyTagId) || string.IsNullOrWhiteSpace(assignment.Group) ||
                string.IsNullOrWhiteSpace(assignment.Value))
                throw new InvalidDataException("Provenance contains an incomplete assignment.");
            if (!byLink.Add(assignment.ContentId.Trim() + "\0" + assignment.MyTagId.Trim()))
                throw new InvalidDataException("Provenance contains a duplicate ContentID↔MyTagID assignment.");
        }
    }

    private static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Database path is empty.");
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
