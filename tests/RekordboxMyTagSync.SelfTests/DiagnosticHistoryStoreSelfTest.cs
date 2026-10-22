using RekordboxMyTagSync.Core;

public static class DiagnosticHistoryStoreSelfTest
{
    public static void Run(string temp)
    {
        var root = Path.Combine(temp, "diagnostic-history-test");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "history.json");
        if (DiagnosticHistoryStore.Load(path).Count != 0)
            throw new InvalidOperationException("Missing history was not empty.");

        const string privatePath = @"C:\Users\Someone\Music\private-file.flac";
        DiagnosticHistoryStore.Append("Source verified: " + privatePath, path);
        DiagnosticHistoryStore.Append("Apply refused: " + privatePath, path);
        if (File.ReadAllText(path).Contains("private-file", StringComparison.Ordinal))
            throw new InvalidOperationException("Raw diagnostic path was persisted.");
        var first = DiagnosticHistoryStore.Load(path);
        if (first.Count != 2 || first[0].Code != "SOURCE_VERIFIED" ||
            first[1].Code != "READ_ONLY_WRITE_BLOCK" ||
            !DiagnosticHistoryStore.FormatForUi(first[0]).Contains("previous", StringComparison.Ordinal))
            throw new InvalidOperationException("Redacted history roundtrip failed.");

        for (var i = 0; i < 205; i++)
            DiagnosticHistoryStore.Append("Path analysis complete.", path);
        var bounded = DiagnosticHistoryStore.Load(path);
        if (bounded.Count != 200 || bounded.Any(x => x.Code != "PATH_ANALYSIS"))
            throw new InvalidOperationException("History was not bounded or rotated.");

        File.WriteAllText(path, "{corrupted");
        if (DiagnosticHistoryStore.Load(path).Count != 0)
            throw new InvalidOperationException("Corrupt history was trusted.");
        DiagnosticHistoryStore.Append("Settings loaded.", path);
        if (DiagnosticHistoryStore.Load(path).Single().Code != "SETTINGS")
            throw new InvalidOperationException("Corrupt history did not recover.");

        File.WriteAllText(path, new string('x', 33 * 1024));
        if (DiagnosticHistoryStore.Load(path).Count != 0)
            throw new InvalidOperationException("Oversized history was trusted.");
        DiagnosticHistoryStore.Append("Restore refused: read-only", path);
        if (DiagnosticHistoryStore.Load(path).Single().Code != "READ_ONLY_WRITE_BLOCK")
            throw new InvalidOperationException("Oversized history did not recover.");
        Console.WriteLine("MYTAGSYNC_REDACTED_DIAGNOSTIC_HISTORY=PASS");
    }
}
