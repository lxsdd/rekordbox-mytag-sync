using System.IO;
using System.Text.RegularExpressions;

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

    private static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Windows path is empty.", nameof(path));

        var candidate = path.Trim();
        if (candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            candidate = DecodeFileUri(candidate);
        else if (candidate.Contains("://", StringComparison.Ordinal))
            throw new NotSupportedException("Only absolute Windows paths and local file URIs are supported.");

        // Path.GetFullPath resolves relative inputs against the EXE's current
        // directory. That must NEVER turn foobar's file://Z:/... into a
        // plausible but fake C:\Program Files\...\file:\Z:\... identity.
        if (!Path.IsPathFullyQualified(candidate))
            throw new ArgumentException("Only fully qualified Windows file paths are accepted.", nameof(path));

        // Unlike TrimEnd('\\'), this retains the separator of drive/UNC roots.
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
    }

    private static string DecodeFileUri(string input)
    {
        var rest = input["file:".Length..].Replace('\\', '/');

        // foobar bridge snapshots can use file://Z:/Music as well as
        // RFC-style file:///Z:/Music. URI parsing alone treats the former
        // inconsistently as a host/drive; recognize the drive explicitly.
        var drive = Regex.Match(
            rest,
            @"^/{0,3}(?<drive>[A-Za-z]):/(?<path>.*)$",
            RegexOptions.CultureInvariant);
        if (drive.Success)
        {
            var tail = DecodeUriPath(drive.Groups["path"].Value);
            return drive.Groups["drive"].Value + @":\" + tail.Replace('/', '\\');
        }

        // Explicit server/share UNC form. Never mistake a drive letter for
        // a remote server; other URI authorities remain unsupported.
        var unc = Regex.Match(
            rest,
            @"^//(?<server>[^/:?#]+)/(?!/)(?<share>[^/?#]+)(?<tail>(?:/[^?#]*)?)$",
            RegexOptions.CultureInvariant);
        if (unc.Success)
        {
            var server = unc.Groups["server"].Value;
            var share = DecodeUriPath(unc.Groups["share"].Value);
            var tail = DecodeUriPath(unc.Groups["tail"].Value);
            return @"\\" + server + @"\" + share + tail.Replace('/', '\\');
        }

        throw new NotSupportedException("Unsupported or ambiguous file URI: absolute drive or UNC path required.");
    }

    private static string DecodeUriPath(string value)
    {
        // Escaped directory separators can silently change the identity
        // structure. Refuse them rather than guessing an alias or file.
        if (Regex.IsMatch(value, @"%(?:2f|5c|00)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new NotSupportedException("Encoded path separators or NUL are not supported.");
        return Uri.UnescapeDataString(value);
    }
}
