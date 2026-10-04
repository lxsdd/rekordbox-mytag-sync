using RekordboxMyTagSync.Core;

static void AssertSequence(string name, IReadOnlyList<string> actual, params string[] expected)
{
    if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        throw new InvalidOperationException($"{name}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
}

var direct = new MappingRule("GENRE", "Genre");
AssertSequence("direct trims and de-duplicates case-insensitively",
    MappingEngine.Apply(direct, new[] { " House ", "house", "Techno", "" }),
    "House", "Techno");

var year = new MappingRule("DATE", "Year", TransformKind.YearFromDate);
AssertSequence("year YYYY", MappingEngine.Apply(year, new[] { "1998" }), "1998");
AssertSequence("year ISO date", MappingEngine.Apply(year, new[] { "1998-04-12" }), "1998");
AssertSequence("year localized date", MappingEngine.Apply(year, new[] { "12.04.1998" }), "1998");
AssertSequence("year embedded", MappingEngine.Apply(year, new[] { "released 1998 remaster" }), "1998");
AssertSequence("year no match", MappingEngine.Apply(year, new[] { "unknown" }));

var regex = new MappingRule("MOOD", "Mood", TransformKind.RegexReplace, Pattern: @"\s+", Replacement: "-");
AssertSequence("regex replace", MappingEngine.Apply(regex, new[] { "Peak Time" }), "Peak-Time");

var prefixed = new MappingRule("COUNTRY", "Country", Prefix: "Country: ");
AssertSequence("prefix", MappingEngine.Apply(prefixed, new[] { "UK" }), "Country: UK");
AssertSequence("ignore empty before prefix", MappingEngine.Apply(prefixed, new[] { "" }));

var joined = new MappingRule("STYLE", "Style", PerValue: false);
AssertSequence("joined multivalue", MappingEngine.Apply(joined, new[] { "Deep", "House" }), "Deep; House");

Console.WriteLine("Mapping self-tests PASS");
