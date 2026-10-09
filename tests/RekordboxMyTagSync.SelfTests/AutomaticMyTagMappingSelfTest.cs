using RekordboxMyTagSync.Core;

public static class AutomaticMyTagMappingSelfTest
{
    public static void Run()
    {
        var tags = new[]
        {
            new RekordboxMyTagDefinition("Y", "Year", null, 0, 0),
            new RekordboxMyTagDefinition("Y2021", "2021", "Y", 0, 0),
            new RekordboxMyTagDefinition("G", "Genre", null, 1, 0),
            new RekordboxMyTagDefinition("GH", "House", "G", 0, 0),
            new RekordboxMyTagDefinition("GT", "Techno", "G", 1, 0)
        };
        var music = new[]
        {
            Track(@"Z:\Music\Singles\A.mp3", "2021-04-16", "House"),
            Track(@"Z:\Music\Singles\B.mp3", "garbage 2021 number", "Techno")
        };
        var advice = AutomaticMyTagMappingAdvisor.Suggest(music, tags);
        if (advice.Rules.Count != 2 ||
            !advice.Rules.Any(x => x.SourceField == "DATE" &&
                x.TargetMyTag == "Year" &&
                x.Transform == TransformKind.StrictYearFromDate) ||
            !advice.Rules.Any(x => x.SourceField == "GENRE" &&
                x.TargetMyTag == "Genre" && x.Transform == TransformKind.Direct) ||
            advice.YearSourceTracks != 2 || advice.GenreSourceTracks != 2)
            throw new InvalidOperationException("Safe standard MyTag suggestions were not generated.");

        var strict = new MappingRule("DATE", "Year", TransformKind.StrictYearFromDate);
        AssertYears(strict, new[] { "2021", "2021-04", "2021-04-16", "2021/04/16", "2021.04.16" },
            "2021");
        AssertYears(strict, new[] {
            "released 2021 remaster", "2021-02-30", "2021-13-01", "2021/00", "2021-1",
            "2021 2022", "no year", "0000", "2021-04-16T12:00:00"
        });

        var wrongYear = MappingEngine.Apply(advice.Rules.Single(x => x.TargetMyTag == "Year"),
            music[1].GetFieldValues("DATE"));
        if (wrongYear.Count != 0)
            throw new InvalidOperationException("Embedded four-digit non-date became an automatic Year.");

        // The real existing PreviewEngine should propose only the values
        // supplied by exact DATE and GENRE fields; no file/DB operations.
        var target = new RekordboxTrackSnapshot("C1", @"R:\Music\Singles\A.mp3",
            new[] { new MyTagAssignment("Year", "2021") });
        var result = PreviewEngine.Create(new PreviewRequest(
            "fixture-automatic-advice",
            new[] { music[0] },
            advice.Rules,
            new[] { target },
            Array.Empty<ManagedAssignment>(),
            new[] { new PathAlias(@"Z:\Music", @"R:\Music") },
            tags));
        if (result.Counts.Additions != 1 ||
            result.Counts.Removals != 0 ||
            result.Counts.AlreadyCorrect != 1 ||
            !result.Details.Any(x => x.Kind == PreviewDetailKind.Add &&
                x.ContentId == "C1" && x.Tag == new MyTagAssignment("Genre", "House")))
            throw new InvalidOperationException("Read-only automatic mappings failed to respect existing manual MyTags.");

        var noYear = AutomaticMyTagMappingAdvisor.Suggest(music,
            tags.Where(x => x.Name != "Year").ToArray());
        if (noYear.Rules.Count != 1 || noYear.Rules[0].TargetMyTag != "Genre")
            throw new InvalidOperationException("Absent Year group was silently invented.");

        var ambiguousGenre = AutomaticMyTagMappingAdvisor.Suggest(music,
            tags.Concat(new[] { new RekordboxMyTagDefinition("G2", "GENRE", null, 2, 0) }).ToArray());
        if (ambiguousGenre.Rules.Any(x => x.TargetMyTag == "Genre"))
            throw new InvalidOperationException("Ambiguous root group was auto-approved.");

        var duplicateChild = AutomaticMyTagMappingAdvisor.Suggest(music,
            tags.Concat(new[] { new RekordboxMyTagDefinition("GH2", "HOUSE", "G", 2, 0) }).ToArray());
        if (duplicateChild.Rules.Any(x => x.TargetMyTag == "Genre"))
            throw new InvalidOperationException("Ambiguous duplicate Genre value was auto-approved.");

        var noMetadata = AutomaticMyTagMappingAdvisor.Suggest(
            new[] { Track(@"Z:\Music\none.mp3", "", "") }, tags);
        if (noMetadata.HasRules)
            throw new InvalidOperationException("Empty source fields were incorrectly auto-mapped.");
    }

    private static void AssertYears(MappingRule rule, string[] inputs, params string[] expected)
    {
        var actual = MappingEngine.Apply(rule, inputs);
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"Strict DATE transform: expected {string.Join(",", expected)}, got {string.Join(",", actual)}.");
    }

    private static BridgeTrack Track(string path, string date, string genre) =>
        new(path, 0,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["date"] = new[] { date }, ["genre"] = new[] { genre }
            },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
}
