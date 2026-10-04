using System.IO;

namespace RekordboxMyTagSync.Core;

public sealed record PathAlias(string SourceRoot, string TargetRoot);

public static class WindowsPathMatcher
{
    public static string Normalize(string path, IReadOnlyList<PathAlias>? aliases = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is empty.", nameof(path));
        var normalized = Canonical(path);
        if (aliases is null || aliases.Count == 0) return normalized;

        var normalizedAliases = NormalizeAliases(aliases);
        PathAlias? best = null;
        foreach (var alias in normalizedAliases)
        {
            if (!IsAtOrBelow(normalized, alias.SourceRoot)) continue;
            if (best is null || alias.SourceRoot.Length > best.SourceRoot.Length) best = alias;
        }

        if (best is null) return normalized;
        var suffix = normalized.Length == best.SourceRoot.Length
            ? string.Empty
            : normalized[best.SourceRoot.Length..].TrimStart('\\', '/');
        return Canonical(Path.Combine(best.TargetRoot, suffix));
    }

    public static bool Equivalent(string left, string right, IReadOnlyList<PathAlias>? aliases = null) =>
        string.Equals(Normalize(left, aliases), Normalize(right, aliases), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<PathAlias> NormalizeAliases(IReadOnlyList<PathAlias> aliases)
    {
        var bySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
        {
            if (alias is null) throw new ArgumentException("Path alias is null.", nameof(aliases));
            if (string.IsNullOrWhiteSpace(alias.SourceRoot) || string.IsNullOrWhiteSpace(alias.TargetRoot))
                throw new ArgumentException("Path alias roots must not be empty.", nameof(aliases));

            var source = Canonical(alias.SourceRoot);
            var target = Canonical(alias.TargetRoot);
            if (bySource.TryGetValue(source, out var existingTarget) &&
                !string.Equals(existingTarget, target, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Conflicting path alias for '{source}'.", nameof(aliases));
            bySource[source] = target;
        }

        return bySource
            .Select(x => new PathAlias(x.Key, x.Value))
            .OrderByDescending(x => x.SourceRoot.Length)
            .ThenBy(x => x.SourceRoot, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsAtOrBelow(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        (path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
         (path[root.Length] == '\\' || path[root.Length] == '/'));

    private static string Canonical(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
