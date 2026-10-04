using System.Text.RegularExpressions;

namespace RekordboxMyTagSync.Core;

public enum TransformKind { Direct, YearFromDate, RegexReplace }
public sealed record MappingRule(string SourceField, string TargetMyTag, TransformKind Transform = TransformKind.Direct, bool PerValue = true, bool IgnoreEmpty = true, string Prefix = "", string? Pattern = null, string Replacement = "");

public static class MappingEngine
{
    private static readonly Regex Year = new(@"(?<!\d)(?<year>\d{4})(?!\d)", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Apply(MappingRule rule, IReadOnlyList<string> sourceValues)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(sourceValues);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.SourceField);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.TargetMyTag);
        if (rule.Transform == TransformKind.RegexReplace && string.IsNullOrEmpty(rule.Pattern))
            throw new ArgumentException("RegexReplace requires a non-empty pattern.", nameof(rule));

        IEnumerable<string> values = rule.PerValue
            ? sourceValues
            : new[] { string.Join("; ", sourceValues.Where(v => !string.IsNullOrWhiteSpace(v))) };
        var output = new List<string>();
        foreach (var raw in values)
        {
            var value = raw?.Trim() ?? string.Empty;
            value = rule.Transform switch
            {
                TransformKind.Direct => value,
                TransformKind.YearFromDate => Year.Match(value) is { Success: true } m ? m.Groups["year"].Value : string.Empty,
                TransformKind.RegexReplace => Regex.Replace(value, rule.Pattern!, rule.Replacement, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)),
                _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Transform, "Unknown transform kind.")
            };
            if (rule.IgnoreEmpty && string.IsNullOrWhiteSpace(value)) continue;
            value = rule.Prefix + value;
            if (!output.Contains(value, StringComparer.OrdinalIgnoreCase)) output.Add(value);
        }
        return output;
    }
}
