using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RekordboxMyTagSync.Core;

if (args.Contains("--verify-remote-access-sources", StringComparer.Ordinal))
{
    var sources = RekordboxDatabaseAccessResolver.ProbePinnedSourcesAsync()
        .GetAwaiter()
        .GetResult();
    Console.WriteLine(
        $"Pinned automatic database-access sources PASS ({sources.Count} available: {string.Join(", ", sources)})");
    return;
}

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

static string WriteBridgeV3Fixture(string directory)
{
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "v3.tsv.gz");
    const string v1Header = "path\tsubsong\tartist\tartists\ttitle\toriginal_title\tremixed_by\talbum\talbum_artist\ttrack_number\ttotal_tracks\tdisc_number\ttotal_discs\tdate\tgenre\tstyle\tbpm\tlabel\tcatalog_number\tduration_seconds\tisrc\tcodec\tbitrate\ttag_fingerprint";
    var header = v1Header + "\textra_metadata_json\tmetadata_vectors_json";
    var row = new[]
    {
        @"Z:\Music\Singles\V3.mp3", "0", "Artist", "", "Title", "", "", "Album", "Artist", "1", "1", "1", "1",
        "2001-01-01", "Techno", "Peak", "132", "Label", "CAT003", "280", "GBABC7654321", "MP3", "320", "abcdef0123456789",
        "{\"MOOD\":[\"Driving\"]}",
        "[{\"name\":\"MOOD\",\"values\":[\"Driving\",\"Driving\"]}]"
    };
    using var file = File.Create(path);
    using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
    using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
    writer.WriteLine(header);
    writer.WriteLine(string.Join('\t', row));
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

    var v3 = BridgeSnapshot.Read(WriteBridgeV3Fixture(temp));
    if (v3.Count != 1 || v3[0].Path != @"Z:\Music\Singles\V3.mp3")
        throw new InvalidOperationException("bridge v3 read failed");
    AssertSequence("bridge v3 core genre", v3[0].GetFieldValues("GENRE"), "Techno");
    AssertSequence("bridge v3 extra metadata remains canonical source", v3[0].GetFieldValues("MOOD"), "Driving");
    AssertSequence("bridge v3 metadata vectors are intentionally not projected", v3[0].GetFieldValues("metadata_vectors_json"));

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

    // Registered Windows installations can have a custom directory name/location.
    // The real executable is mandatory; a registry display name alone is insufficient.
    var registeredRoot = Path.Combine(temp, "CustomDJInstall");
    Directory.CreateDirectory(registeredRoot);
    File.WriteAllBytes(Path.Combine(registeredRoot, "rekordbox.exe"), new byte[] { 0x4d, 0x5a });
    var registeredRb6 = RekordboxDiscovery.InspectRegisteredInstallation(
        "rekordbox", "6.6.11", registeredRoot, null);
    if (registeredRb6 is null || registeredRb6.MajorVersion != 6 ||
        registeredRb6.Version != "6.6.11" ||
        !string.Equals(registeredRb6.DirectoryPath, registeredRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Custom registered rekordbox 6 installation was not recognized.");
    var registeredRb7 = RekordboxDiscovery.InspectRegisteredInstallation(
        "rekordbox", "7.1.4", null, Path.Combine(registeredRoot, "rekordbox.exe") + ",0");
    if (registeredRb7 is null || registeredRb7.MajorVersion != 7)
        throw new InvalidOperationException("DisplayIcon-based registered rekordbox 7 installation was not recognized.");
    if (RekordboxDiscovery.InspectRegisteredInstallation(
        "rekordboxAgent", "6.6.11", registeredRoot, null) is not null)
        throw new InvalidOperationException("Unrelated rekordboxAgent must not count as an installation.");
    if (RekordboxDiscovery.InspectRegisteredInstallation(
        "rekordbox", "6.6.11", Path.Combine(temp, "MissingInstall"), null) is not null)
        throw new InvalidOperationException("Missing executable must not authorize a registered installation.");
    if (RekordboxDiscovery.InspectRegisteredInstallation(
        "rekordbox", "5.9.9", registeredRoot, null) is not null)
        throw new InvalidOperationException("Unsupported rekordbox version must not count as qualified evidence.");

    var discovery = RekordboxDiscovery.Discover(new RekordboxDiscoveryOptions(programRoot, appRoot));
    if (discovery.Installations.Count != 2 || discovery.Installations[0].MajorVersion != 6 || discovery.Installations[1].MajorVersion != 7)
        throw new InvalidOperationException("rekordbox 6/7 installation discovery failed");
    if (discovery.Libraries.Count != 1 || !discovery.Libraries[0].Safe || discovery.Libraries[0].Evidence.Count != 2 || discovery.Libraries[0].UsedBy.Count != 2)
        throw new InvalidOperationException("same rekordbox database was not deduplicated across rb6/rb7 evidence");
    var expectedDatabasePath = Path.GetFullPath(dbPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (!string.Equals(discovery.Libraries[0].DatabasePath, expectedDatabasePath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("discovered database path mismatch");

    var manualTarget = RekordboxDiscovery.InspectManual(
        dbPath,
        new RekordboxDiscoveryOptions(programRoot, appRoot));
    if (!manualTarget.Safe ||
        manualTarget.UsedBy.Count != 2 ||
        manualTarget.Evidence.Count != 1 ||
        manualTarget.Evidence[0] != "manual browse selection" ||
        !string.Equals(manualTarget.DatabasePath, expectedDatabasePath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("manual rekordbox target qualification failed");

    PathMatcherSelfTest.Run(temp);
    PathMatchAdvisorSelfTest.Run(temp);
    PathPairVerifierSelfTest.Run(temp);
    PhysicalFileIdentityVerifierSelfTest.Run(temp);
    PreviewSelfTest.Run(temp);
    RekordboxMutationPreflightSelfTest.Run(temp);
    RekordboxMutationVerificationSelfTest.Run(temp);
    RekordboxMutationTransactionSelfTest.Run();
    RekordboxSongMyTagWriteSemanticsSelfTest.Run();
    RekordboxSongMyTagWriterSelfTest.Run();
    RekordboxUpdateCounterSelfTest.Run();
    RekordboxMyTagDefinitionWriterSelfTest.Run();
    RekordboxMutationExecutorSelfTest.Run(temp);
    RekordboxDatabaseAccessResolverSelfTest.Run(temp);
    AppSettingsStoreSelfTest.Run(temp);
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

Console.WriteLine("Mapping + bridge + rekordbox discovery + preview + mutation preflight + transaction rollback + runtime SongMyTag semantics/writer + postimage/provenance/integrity + update counter + fail-closed MyTag definition creation + encrypted transactional executor/restore + automatic database access + encrypted database + provenance + rolling backup/restore self-tests PASS");
