using System.IO;

namespace RekordboxMyTagSync.Core;

/// <summary>
/// Rekordbox may encode a top-level djmdMyTag group with either NULL
/// (older/local fixtures) or the reserved literal "root" (real libraries).
/// Only that exact sentinel is an implicit parent. Every other non-null
/// ParentID must resolve to an active definition.
/// </summary>
internal static class RekordboxMyTagHierarchy
{
    internal const string RootSentinel = "root";

    internal static bool IsTopLevel(string? parentId) =>
        parentId is null || string.Equals(parentId, RootSentinel, StringComparison.Ordinal);

    internal static string? NormalizeForSnapshot(string? parentId) =>
        IsTopLevel(parentId) ? null : parentId;

    internal static void RejectRootIdCollision(IEnumerable<string> ids)
    {
        if (ids.Any(id => string.Equals(id, RootSentinel, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                "MyTag ID 'root' collides with the reserved top-level parent sentinel.");
    }

    /// <summary>
    /// Creating new top-level definitions is only safe when the active
    /// library uses one consistent NULL/root convention.
    /// </summary>
    internal static string? QualifyStoredRootParent(IEnumerable<string?> parents)
    {
        var styles = parents.Where(IsTopLevel).Select(x => x is null ? 0 : 1)
            .Distinct().ToArray();
        if (styles.Length != 1)
            throw new InvalidDataException(
                "MyTag root-group ParentID convention is missing or mixed (NULL/root).");
        return styles[0] == 0 ? null : RootSentinel;
    }
}
