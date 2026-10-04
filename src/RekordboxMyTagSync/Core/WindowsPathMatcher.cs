namespace RekordboxMyTagSync.Core;

public sealed record PathAlias(string SourceRoot, string TargetRoot);

public static class WindowsPathMatcher
{
    public static string Normalize(string path, IReadOnlyList<PathAlias>? aliases = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is empty.", nameof(path));
        var normalized = Canonical(path);
        if (aliases is null) return normalized;

        PathAlias? best = null;
        foreach (var alias in aliases)
        {
            var source = Canonical(alias.SourceRoot);
            if (!IsAtOrBelow(normalized, source)) continue;
            if (best is null || source.Length > Canonical(best.SourceRoot).Length) best = alias;
        }

        if (best is null) return normalized;
        var bestSource = Canonical(best.SourceRoot);
        var suffix = normalized.Length == bestSource.Length ? string.Empty : normalized[bestSource.Length..].TrimStart('\\', '/');
        return Canonical(Path.Combine(Canonical(best.TargetRoot), suffix));
    }

    public static bool Equivalent(string left, string right, IReadOnlyList<PathAlias>? aliases = null) =>
        string.Equals(Normalize(left, aliases), Normalize(right, aliases), StringComparison.OrdinalIgnoreCase);

    private static bool IsAtOrBelow(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        (path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
         (path[root.Length] == '\\' || path[root.Length] == '/'));

    private static string Canonical(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
