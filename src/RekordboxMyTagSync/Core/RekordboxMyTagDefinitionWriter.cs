using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RekordboxMyTagSync.Core;

internal enum RekordboxDefinitionIdKind
{
    Numeric,
    Guid
}

internal sealed record RekordboxMyTagDefinitionWriteProfile(
    long DataStatus,
    long LocalDataStatus,
    int RootAttribute,
    int ChildAttribute,
    int FirstChildSequence,
    RekordboxDefinitionIdKind IdKind,
    string? StoredRootParentId = null);

internal sealed record RekordboxCreatedMyTagDefinition(
    string Id,
    string Name,
    string? ParentId,
    int Sequence,
    int Attribute,
    long LocalUsn);

internal sealed record RekordboxDefinitionWriteResult(
    IReadOnlyList<RekordboxCreatedMyTagDefinition> Created,
    long NextLocalUsn);

internal static class RekordboxMyTagDefinitionWriter
{
    internal static RekordboxMyTagDefinitionWriteProfile Qualify(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("SQLite connection must be open before qualifying MyTag definition writes.");

        var columns = ReadColumnDefaults(connection, transaction);
        var defaultDataStatus = RequireIntegerDefault(columns, "rb_data_status");
        var defaultLocalDataStatus = RequireIntegerDefault(columns, "rb_local_data_status");
        if (RequireIntegerDefault(columns, "rb_local_deleted") != 0)
            throw new InvalidDataException("djmdMyTag rb_local_deleted default is not the active state.");
        if (RequireIntegerDefault(columns, "rb_local_synced") != 0)
            throw new InvalidDataException("djmdMyTag rb_local_synced default is not the local-pending state.");
        _ = RequireIntegerDefault(columns, "usn");
        RequireAnyDefault(columns, "created_at");
        RequireAnyDefault(columns, "updated_at");

        var rows = ReadRows(connection, transaction);
        var active = rows.Where(x => !x.LocalDeleted).ToArray();
        if (active.Length == 0)
            throw new InvalidDataException("Cannot qualify MyTag definition creation without active djmdMyTag evidence rows.");

        var statuses = active
            .Select(x => (x.DataStatus, x.LocalDataStatus))
            .Distinct()
            .ToArray();
        if (statuses.Length != 1 ||
            statuses[0].DataStatus != defaultDataStatus ||
            statuses[0].LocalDataStatus != defaultLocalDataStatus)
            throw new InvalidDataException(
                "djmdMyTag active status semantics do not match unambiguous schema defaults.");

        RekordboxMyTagHierarchy.RejectRootIdCollision(rows.Select(x => x.Id));
        var roots = active.Where(x => RekordboxMyTagHierarchy.IsTopLevel(x.ParentId)).ToArray();
        var children = active.Where(x => !RekordboxMyTagHierarchy.IsTopLevel(x.ParentId)).ToArray();
        if (roots.Length == 0 || children.Length == 0)
            throw new InvalidDataException(
                "Cannot qualify MyTag definition creation without both root-group and child-value evidence.");

        var storedRootParentId = RekordboxMyTagHierarchy.QualifyStoredRootParent(
            roots.Select(x => x.ParentId));
        var rootIds = roots.Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (children.Any(x => x.ParentId is null || !rootIds.Contains(x.ParentId)))
            throw new InvalidDataException(
                "Cannot create MyTag definitions with unknown or nested parent groups.");

        var rootAttribute = UniqueAttribute(roots, "root-group");
        var childAttribute = UniqueAttribute(children, "child-value");

        ValidateSequenceSet(roots, "root-group");
        var childStarts = new List<int>();
        foreach (var group in children.GroupBy(x => x.ParentId!, StringComparer.OrdinalIgnoreCase))
        {
            var siblings = group.ToArray();
            ValidateSequenceSet(siblings, $"child values under parent '{group.Key}'");
            childStarts.Add(siblings.Min(x => x.Sequence));
        }
        var distinctStarts = childStarts.Distinct().ToArray();
        if (distinctStarts.Length != 1)
            throw new InvalidDataException(
                "Cannot qualify the first child MyTag sequence: observed sibling groups use different starting sequences.");

        var ids = rows.Select(x => x.Id).ToArray();
        var numericIds = ids.All(x => ulong.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        var guidIds = ids.All(x => Guid.TryParse(x, out _));
        if (numericIds == guidIds)
            throw new InvalidDataException("djmdMyTag ID scheme is mixed or ambiguous.");

        if (rows.Any(x => !Guid.TryParse(x.Uuid, out _)))
            throw new InvalidDataException(
                "djmdMyTag UUID semantics are not qualified because an existing UUID is not a GUID.");

        return new RekordboxMyTagDefinitionWriteProfile(
            defaultDataStatus,
            defaultLocalDataStatus,
            rootAttribute,
            childAttribute,
            distinctStarts[0],
            numericIds ? RekordboxDefinitionIdKind.Numeric : RekordboxDefinitionIdKind.Guid,
            storedRootParentId);
    }

    internal static RekordboxDefinitionWriteResult EnsureDefinitions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<MyTagAssignment> requested,
        RekordboxMyTagDefinitionWriteProfile profile,
        long firstLocalUsn)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(profile);
        if (firstLocalUsn < 0)
            throw new ArgumentOutOfRangeException(nameof(firstLocalUsn));

        var desired = requested
            .Select(Normalize)
            .Distinct(MyTagComparer.Instance)
            .OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (desired.Length == 0)
            return new RekordboxDefinitionWriteResult(
                Array.Empty<RekordboxCreatedMyTagDefinition>(),
                firstLocalUsn);

        var rows = ReadRows(connection, transaction)
            .Where(x => !x.LocalDeleted)
            .ToList();
        var allIds = ReadAllIds(connection, transaction);
        RekordboxMyTagHierarchy.RejectRootIdCollision(allIds);
        ValidateIdScheme(allIds, profile.IdKind);

        // A changed NULL/root convention invalidates a previously
        // qualified writer profile. Never silently mix parent encodings.
        var rootConvention = RekordboxMyTagHierarchy.QualifyStoredRootParent(
            rows.Where(x => RekordboxMyTagHierarchy.IsTopLevel(x.ParentId))
                .Select(x => x.ParentId));
        if (!string.Equals(rootConvention, profile.StoredRootParentId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "MyTag top-level parent convention changed after qualification.");

        var created = new List<RekordboxCreatedMyTagDefinition>();
        var nextUsn = firstLocalUsn;

        foreach (var groupName in desired.Select(x => x.Group)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var parents = rows
                .Where(x => RekordboxMyTagHierarchy.IsTopLevel(x.ParentId) &&
                            string.Equals(x.Name, groupName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (parents.Length > 1)
                throw new InvalidDataException($"MyTag group '{groupName}' is ambiguous.");
            if (parents.Length == 0)
            {
                var seq = NextSequence(
                    rows.Where(x => RekordboxMyTagHierarchy.IsTopLevel(x.ParentId)),
                    profile.StoredRootParentId, "root-group");
                var row = Insert(
                    connection,
                    transaction,
                    allIds,
                    profile.IdKind,
                    groupName,
                    parentId: profile.StoredRootParentId,
                    seq,
                    profile.RootAttribute,
                    nextUsn);
                rows.Add(row);
                created.Add(ToCreated(row, nextUsn));
                nextUsn = CheckedNextUsn(nextUsn);
            }
        }

        foreach (var tag in desired)
        {
            var parent = rows.Single(x =>
                RekordboxMyTagHierarchy.IsTopLevel(x.ParentId) &&
                string.Equals(x.Name, tag.Group, StringComparison.OrdinalIgnoreCase));
            var matches = rows
                .Where(x => string.Equals(x.ParentId, parent.Id, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(x.Name, tag.Value, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length > 1)
                throw new InvalidDataException($"MyTag value '{tag.Group}/{tag.Value}' is ambiguous.");
            if (matches.Length == 1)
                continue;

            var siblings = rows
                .Where(x => string.Equals(x.ParentId, parent.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var seq = siblings.Length == 0
                ? profile.FirstChildSequence
                : NextSequence(siblings, parent.Id, $"children of '{tag.Group}'");
            var row = Insert(
                connection,
                transaction,
                allIds,
                profile.IdKind,
                tag.Value,
                parent.Id,
                seq,
                profile.ChildAttribute,
                nextUsn);
            rows.Add(row);
            created.Add(ToCreated(row, nextUsn));
            nextUsn = CheckedNextUsn(nextUsn);
        }

        return new RekordboxDefinitionWriteResult(created, nextUsn);
    }

    private static DefinitionRow Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HashSet<string> allIds,
        RekordboxDefinitionIdKind idKind,
        string name,
        string? parentId,
        int sequence,
        int attribute,
        long localUsn)
    {
        var id = AllocateId(allIds, idKind);
        var uuid = AllocateUuid(connection, transaction);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO djmdMyTag(
                ID, UUID, Seq, Name, Attribute, ParentID, rb_local_usn)
            VALUES($id, $uuid, $seq, $name, $attribute, $parent, $localUsn);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$uuid", uuid);
        command.Parameters.AddWithValue("$seq", sequence);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$attribute", attribute);
        command.Parameters.AddWithValue("$parent", parentId is null ? DBNull.Value : parentId);
        command.Parameters.AddWithValue("$localUsn", localUsn);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("MyTag definition insert did not create exactly one row.");

        allIds.Add(id);
        return new DefinitionRow(
            id,
            uuid,
            sequence,
            name,
            attribute,
            parentId,
            LocalDeleted: false,
            DataStatus: 0,
            LocalDataStatus: 0);
    }

    private static string AllocateId(
        HashSet<string> allIds,
        RekordboxDefinitionIdKind kind)
    {
        if (kind == RekordboxDefinitionIdKind.Numeric)
        {
            ulong maximum = 0;
            foreach (var id in allIds)
            {
                if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    throw new InvalidDataException("djmdMyTag numeric ID scheme drifted after qualification.");
                maximum = Math.Max(maximum, value);
            }
            if (maximum == ulong.MaxValue)
                throw new InvalidDataException("djmdMyTag numeric ID space is exhausted.");
            return checked(maximum + 1).ToString(CultureInfo.InvariantCulture);
        }

        if (allIds.Any(x => !Guid.TryParse(x, out _)))
            throw new InvalidDataException("djmdMyTag GUID ID scheme drifted after qualification.");
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Guid.NewGuid().ToString();
            if (!allIds.Contains(candidate))
                return candidate;
        }
        throw new InvalidDataException("Could not allocate a unique djmdMyTag GUID ID.");
    }

    private static string AllocateUuid(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var existing = new HashSet<Guid>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT UUID FROM djmdMyTag WHERE UUID IS NOT NULL AND TRIM(UUID) <> '';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var value = reader.GetString(0).Trim();
                if (!Guid.TryParse(value, out var parsed))
                    throw new InvalidDataException("djmdMyTag UUID scheme drifted after qualification.");
                existing.Add(parsed);
            }
        }

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Guid.NewGuid();
            if (!existing.Contains(candidate))
                return candidate.ToString();
        }
        throw new InvalidDataException("Could not allocate a unique djmdMyTag UUID.");
    }

    private static int NextSequence(
        IEnumerable<DefinitionRow> source,
        string? parentId,
        string label)
    {
        var rows = source.ToArray();
        if (rows.Length == 0)
            throw new InvalidDataException($"Cannot infer next {label} sequence from an empty evidence set.");
        ValidateSequenceSet(rows, label);
        var maximum = rows.Max(x => x.Sequence);
        try
        {
            return checked(maximum + 1);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException($"{label} sequence overflow.", ex);
        }
    }

    private static void ValidateSequenceSet(
        IReadOnlyList<DefinitionRow> rows,
        string label)
    {
        if (rows.Any(x => x.Sequence < 0))
            throw new InvalidDataException($"{label} contains a negative Seq.");
        if (rows.Select(x => x.Sequence).Distinct().Count() != rows.Count)
            throw new InvalidDataException($"{label} contains duplicate Seq values.");
    }

    private static int UniqueAttribute(
        IReadOnlyList<DefinitionRow> rows,
        string label)
    {
        var values = rows.Select(x => x.Attribute).Distinct().ToArray();
        if (values.Length != 1)
            throw new InvalidDataException(
                $"Cannot qualify MyTag {label} Attribute: observed {values.Length} distinct values.");
        return values[0];
    }

    private static HashSet<string> ReadAllIds(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ID FROM djmdMyTag;";
        using var reader = command.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
                throw new InvalidDataException("djmdMyTag contains a NULL ID.");
            var id = reader.GetString(0).Trim();
            if (id.Length == 0 || !ids.Add(id))
                throw new InvalidDataException("djmdMyTag contains an empty or duplicate ID.");
        }
        if (ids.Count == 0)
            throw new InvalidDataException("Cannot qualify djmdMyTag ID generation from an empty table.");
        return ids;
    }

    private static void ValidateIdScheme(
        IEnumerable<string> ids,
        RekordboxDefinitionIdKind expected)
    {
        var values = ids.ToArray();
        var numeric = values.All(x => ulong.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        var guid = values.All(x => Guid.TryParse(x, out _));
        var actual = numeric != guid
            ? numeric ? RekordboxDefinitionIdKind.Numeric : RekordboxDefinitionIdKind.Guid
            : throw new InvalidDataException("djmdMyTag ID scheme is mixed or ambiguous.");
        if (actual != expected)
            throw new InvalidDataException("djmdMyTag ID scheme changed after qualification.");
    }

    private static Dictionary<string, string?> ReadColumnDefaults(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info([djmdMyTag]);";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var name = reader.GetString(1);
            result[name] = reader.IsDBNull(4)
                ? null
                : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture);
        }
        if (result.Count == 0)
            throw new InvalidDataException("djmdMyTag schema is unavailable.");
        return result;
    }

    private static long RequireIntegerDefault(
        IReadOnlyDictionary<string, string?> defaults,
        string column)
    {
        RequireAnyDefault(defaults, column);
        var raw = defaults[column]!.Trim();
        while (raw.Length >= 2 && raw[0] == '(' && raw[^1] == ')')
            raw = raw[1..^1].Trim();
        raw = raw.Trim('\'', '"');
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException(
                $"djmdMyTag column '{column}' default is not an unambiguous integer literal.");
        return value;
    }

    private static void RequireAnyDefault(
        IReadOnlyDictionary<string, string?> defaults,
        string column)
    {
        if (!defaults.TryGetValue(column, out var value))
            throw new InvalidDataException($"djmdMyTag column '{column}' is missing.");
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException(
                $"djmdMyTag column '{column}' has no schema default; creation remains fail-closed.");
    }

    private static List<DefinitionRow> ReadRows(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ID, UUID, Seq, Name, Attribute, ParentID,
                   COALESCE(rb_local_deleted, 0), rb_data_status, rb_local_data_status
            FROM djmdMyTag;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<DefinitionRow>();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) ||
                reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(7) || reader.IsDBNull(8))
                throw new InvalidDataException("djmdMyTag contains NULL in a required definition field.");

            var id = reader.GetString(0).Trim();
            var uuid = reader.GetString(1).Trim();
            var name = reader.GetString(3).Trim();
            if (id.Length == 0 || uuid.Length == 0 || name.Length == 0)
                throw new InvalidDataException("djmdMyTag contains an empty identity/name field.");

            result.Add(new DefinitionRow(
                id,
                uuid,
                Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                name,
                Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
                Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture) != 0,
                Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(8), CultureInfo.InvariantCulture)));
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (result.Any(x => !ids.Add(x.Id)))
            throw new InvalidDataException("djmdMyTag contains duplicate IDs.");
        return result;
    }

    private static MyTagAssignment Normalize(MyTagAssignment assignment)
    {
        if (assignment is null ||
            string.IsNullOrWhiteSpace(assignment.Group) ||
            string.IsNullOrWhiteSpace(assignment.Value))
            throw new InvalidDataException("Requested MyTag definition contains an empty group or value.");
        return new MyTagAssignment(assignment.Group.Trim(), assignment.Value.Trim());
    }

    private static long CheckedNextUsn(long current)
    {
        try
        {
            return checked(current + 1);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("MyTag definition rb_local_usn overflow.", ex);
        }
    }

    private static RekordboxCreatedMyTagDefinition ToCreated(
        DefinitionRow row,
        long localUsn) =>
        new(row.Id, row.Name, row.ParentId, row.Sequence, row.Attribute, localUsn);

    private sealed record DefinitionRow(
        string Id,
        string Uuid,
        int Sequence,
        string Name,
        int Attribute,
        string? ParentId,
        bool LocalDeleted,
        long DataStatus,
        long LocalDataStatus);

    private sealed class MyTagComparer : IEqualityComparer<MyTagAssignment>
    {
        internal static readonly MyTagComparer Instance = new();

        public bool Equals(MyTagAssignment? x, MyTagAssignment? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null &&
             string.Equals(x.Group, y.Group, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase));

        public int GetHashCode(MyTagAssignment obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Group),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value));
    }
}
