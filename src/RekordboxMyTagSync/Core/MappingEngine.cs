using System.Globalization;
using System.Text.RegularExpressions;

namespace RekordboxMyTagSync.Core;

public enum TransformKind { Direct, YearFromDate, RegexReplace, StrictYearFromDate }
public sealed record MappingRule(string SourceField, string TargetMyTag, TransformKind Transform = TransformKind.Direct, bool PerValue = true, bool IgnoreEmpty = true, string Prefix = "", string? Pattern = null, string Replacement = "");

public static class MappingEngine
{
    private static readonly Regex Year = new(@"(?<!\d)(?<year>\d{4})(?!\d)", RegexOptions.CultureInvariant);
    // Strict year extraction prevents an arbitrary four-digit number embedded
    // in free text from being treated as a release year.
    private static readonly Regex StrictDate = new(
        @"^(?<year>\d{4})(?:[-/.](?<month>\d{2})(?:[-/.](?<day>\d{2}))?)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
                TransformKind.StrictYearFromDate => ParseStrictYear(value),
                TransformKind.RegexReplace => Regex.Replace(value, rule.Pattern!, rule.Replacement, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)),
                _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Transform, "Unknown transform kind.")
            };
            if (rule.IgnoreEmpty && string.IsNullOrWhiteSpace(value)) continue;
            value = rule.Prefix + value;
            if (!output.Contains(value, StringComparer.OrdinalIgnoreCase)) output.Add(value);
        }
        return output;
    }

    private static string ParseStrictYear(string raw)
    {
        var match = StrictDate.Match(raw);
        if (!match.Success || !int.TryParse(match.Groups["year"].Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year < 1000)
            return string.Empty;
        var month = match.Groups["month"];
        if (month.Success)
        {
            if (!int.TryParse(month.Value, out var mm) || mm is < 1 or > 12)
                return string.Empty;
            var day = match.Groups["day"];
            if (day.Success && (!int.TryParse(day.Value, out var dd) || dd < 1 ||
                                dd > DateTime.DaysInMonth(year, mm)))
                return string.Empty;
        }
        return match.Groups["year"].Value;
    }
}
