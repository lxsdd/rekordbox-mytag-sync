using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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

static void WriteRekordboxSettings(string appRoot, string databaseDirectory)
{
    var dir = Path.Combine(appRoot, "rekordbox6");
    Directory.CreateDirectory(dir);
    new XDocument(new XElement("ROOT",
        new XElement("VALUE", new XAttribute("name", "masterDbDirectory"), new XAttribute("val", databaseDirectory))))
        .Save(Path.Combine(dir, "rekordbox3.settings"));
}

static void WriteRekordboxAgentOptions(string appRoot, string databasePath)
{
    var dir = Path.Combine(appRoot, "rekordboxAgent", "storage");
    Directory.CreateDirectory(dir);
    var payload = JsonSerializer.Serialize(new { options = new object[][] { new object[] { "db-path", databasePath } } });
    File.WriteAllText(Path.Combine(dir, "options.json"), payload, new UTF8Encoding(false));
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

    var programRoot = Path.Combine(temp, "Program Files");
    Directory.CreateDirectory(Path.Combine(programRoot, "Pioneer", "rekordbox 6.8.5"));
    Directory.CreateDirectory(Path.Combine(programRoot, "rekordbox", "rekordbox 7.1.4"));
    var appRoot = Path.Combine(temp, "AppData", "Roaming", "Pioneer");
    var dbDir = Path.Combine(temp, "LibraryA");
    Directory.CreateDirectory(dbDir);
    var dbPath = Path.Combine(dbDir, "master.db");
    File.WriteAllBytes(dbPath, new byte[] { 1, 2, 3 });
    WriteRekordboxSettings(appRoot, dbDir);
    WriteRekordboxAgentOptions(appRoot, dbPath);

    var discovery = RekordboxDiscovery.Discover(new RekordboxDiscoveryOptions(programRoot, appRoot));
    if (discovery.Installations.Count != 2 || discovery.Installations[0].MajorVersion != 6 || discovery.Installations[1].MajorVersion != 7)
        throw new InvalidOperationException("rekordbox 6/7 installation discovery failed");
    if (discovery.Libraries.Count != 1 || !discovery.Libraries[0].Safe || discovery.Libraries[0].Evidence.Count != 2 || discovery.Libraries[0].UsedBy.Count != 2)
        throw new InvalidOperationException("same rekordbox database was not deduplicated across rb6/rb7 evidence");
    var expectedDatabasePath = Path.GetFullPath(dbPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (!string.Equals(discovery.Libraries[0].DatabasePath, expectedDatabasePath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("discovered database path mismatch");

    PathMatcherSelfTest.Run(temp);
    PreviewSelfTest.Run(temp);
    RekordboxMutationPreflightSelfTest.Run(temp);
    RekordboxMutationVerificationSelfTest.Run(temp);
    RekordboxMutationTransactionSelfTest.Run();
    RekordboxSongMyTagWriteSemanticsSelfTest.Run();
    RekordboxUpdateCounterSelfTest.Run();
    RekordboxDatabaseSelfTest.Run(temp);
    ProvenanceSelfTest.Run(temp);
    BackupRestoreSelfTest.Run(temp);

    var otherDbDir = Path.Combine(temp, "LibraryB");
    Directory.CreateDirectory(otherDbDir);
    var otherDb = Path.Combine(otherDbDir, "master.db");
    File.WriteAllBytes(otherDb, new byte[] { 4, 5, 6 });
    WriteRekordboxAgentOptions(appRoot, otherDb);
    var conflict = RekordboxDiscovery.Discover(new RekordboxDiscoveryOptions(programRoot, appRoot));
    if (conflict.Libraries.Count != 2 || conflict.Libraries.Any(x => x.Safe) || !conflict.Diagnostics.Any(x => x.Contains("database-path conflict", StringComparison.Ordinal)))
        throw new InvalidOperationException("settings/options database conflict did not fail closed");
}
finally
{
    try { Directory.Delete(temp, true); } catch { }
}

Console.WriteLine("Mapping + bridge + rekordbox discovery + preview + mutation preflight + transaction rollback + runtime SongMyTag semantics + postimage/provenance/integrity + update counter + encrypted database + provenance + rolling backup/restore self-tests PASS");
