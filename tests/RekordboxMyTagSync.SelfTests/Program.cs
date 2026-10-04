using System.IO.Compression;
using System.Text;
using RekordboxMyTagSync.Core;

static void AssertSequence(string name, IReadOnlyList<string> actual, params string[] expected)
{
    if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        throw new InvalidOperationException($"{name}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
}

static string WriteBridgeFixture(string directory, bool schemaV2, string? extraJson = null)
{
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, schemaV2 ? "v2.tsv.gz" : "v1.tsv.gz");
    const string v1Header = "path\tsubsong\tartist\tartists\ttitle\toriginal_title\tremixed_by\talbum\talbum_artist\ttrack_number\ttotal_tracks\tdisc_number\ttotal_discs\tdate\tgenre\tstyle\tbpm\tlabel\tcatalog_number\tduration_seconds\tisrc\tcodec\tbitrate\ttag_fingerprint";
    var header = schemaV2 ? v1Header + "\textra_metadata_json" : v1Header;
    var row = new[]
    {
        @"Z:\Music\Singles\Test.mp3", "0", "Artist", "", "Title", "", "", "Album", "Artist", "1", "1", "1", "1",
        "1998-04-12", "House", "Deep House", "128", "Label", "CAT001", "300", "GBABC1234567", "MP3", "320", "0123456789abcdef"
    };
    var line = string.Join('\t', row);
    if (schemaV2) line += "\t" + (extraJson ?? "{}");
    using var file = File.Create(path);
    using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
    using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
    writer.WriteLine(header);
    writer.WriteLine(line);
    return path;
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

var temp = Path.Combine(Path.GetTempPath(), "rekordbox-mytag-sync-selftest-" + Guid.NewGuid().ToString("N"));
try
{
    var v1 = BridgeSnapshot.Read(WriteBridgeFixture(temp, false));
    if (v1.Count != 1 || v1[0].Path != @"Z:\Music\Singles\Test.mp3") throw new InvalidOperationException("bridge v1 read failed");
    AssertSequence("bridge v1 core genre", v1[0].GetFieldValues("GENRE"), "House");
    AssertSequence("bridge v1 has no extras", v1[0].GetFieldValues("MOOD"));

    var v2 = BridgeSnapshot.Read(WriteBridgeFixture(temp, true, "{\"MOOD\":[\"Euphoric\",\"Dark\"],\"CUSTOM_TAG\":[\"Foo\",\"Bar\"]}"));
    if (v2.Count != 1) throw new InvalidOperationException("bridge v2 read failed");
    AssertSequence("bridge v2 core genre", v2[0].GetFieldValues("genre"), "House");
    AssertSequence("bridge v2 true multivalue extra", v2[0].GetFieldValues("mood"), "Euphoric", "Dark");
    AssertSequence("bridge v2 custom multivalue extra", v2[0].GetFieldValues("CUSTOM_TAG"), "Foo", "Bar");

    var duplicateCore = WriteBridgeFixture(temp, true, "{\"GENRE\":[\"Techno\"]}");
    var rejectedCoreDuplicate = false;
    try { _ = BridgeSnapshot.Read(duplicateCore); }
    catch (InvalidDataException) { rejectedCoreDuplicate = true; }
    if (!rejectedCoreDuplicate) throw new InvalidOperationException("bridge v2 core/extra duplicate was not rejected");

    var emptyExtra = WriteBridgeFixture(temp, true, "");
    var rejectedEmptyExtra = false;
    try { _ = BridgeSnapshot.Read(emptyExtra); }
    catch (InvalidDataException) { rejectedEmptyExtra = true; }
    if (!rejectedEmptyExtra) throw new InvalidOperationException("bridge v2 empty extra JSON was not rejected");
}
finally
{
    try { Directory.Delete(temp, true); } catch { }
}

Console.WriteLine("Mapping + bridge contract self-tests PASS");
