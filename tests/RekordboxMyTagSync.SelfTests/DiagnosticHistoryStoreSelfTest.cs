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

        // A safe support report must be reproducible, deliberately
        // incomplete, and contain ONLY fixed category counters.
        var beforeReport = File.ReadAllBytes(path);
        var safeReport = DiagnosticHistoryStore.BuildRedactedSupportReport(first);
        var expectedReport = DiagnosticHistoryStore.BuildRedactedSupportReport(first);
        if (safeReport != expectedReport ||
            !safeReport.StartsWith("MYTAGSYNC_REDACTED_SUPPORT_REPORT=V1", StringComparison.Ordinal) ||
            !safeReport.Contains("EVENTS=2", StringComparison.Ordinal) ||
            !safeReport.Contains("CATEGORY=READ_ONLY_WRITE_BLOCK COUNT=1", StringComparison.Ordinal) ||
            !safeReport.Contains("CATEGORY=SOURCE_VERIFIED COUNT=1", StringComparison.Ordinal) ||
            !safeReport.Contains("REAL_WINDOWS11_X64_ACCEPTANCE=NOT_ATTESTED", StringComparison.Ordinal) ||
            !safeReport.Contains("REKORDBOX6_7_XDJ_RX3_ACCEPTANCE=NOT_ATTESTED", StringComparison.Ordinal) ||
            !safeReport.Contains("DISTRIBUTION_PERMISSION=NOT_ATTESTED_BY_REPORT", StringComparison.Ordinal) ||
            safeReport.Contains("Someone", StringComparison.Ordinal) ||
            safeReport.Contains("private-file", StringComparison.Ordinal) ||
            !File.ReadAllBytes(path).SequenceEqual(beforeReport))
            throw new InvalidOperationException("Support report leaked private data, claimed acceptance or touched saved history.");

        var emptyReport = DiagnosticHistoryStore.BuildRedactedSupportReport(Array.Empty<DiagnosticHistoryStore.Entry>());
        if (!emptyReport.Contains("EVENTS=0", StringComparison.Ordinal) ||
            !emptyReport.Contains("HISTORY_COMPLETENESS=NOT_ATTESTED", StringComparison.Ordinal))
            throw new InvalidOperationException("Empty support report implied completed acceptance.");

        // Never serialize an arbitrary event code, even if supplied by a
        // malicious caller rather than by the on-disk allowlisted parser.
        var malicious = new DiagnosticHistoryStore.Entry(
            DateTimeOffset.UtcNow, "SOURCE_VERIFIED\\nSECRET=C:\\private-music");
        var invalidUtc = new DiagnosticHistoryStore.Entry(
            DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(2)), "SOURCE_VERIFIED");
        foreach (var candidate in new[] { malicious, invalidUtc })
        {
            var rejected = false;
            try { DiagnosticHistoryStore.BuildRedactedSupportReport(new[] { candidate }); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException("Untrusted support report input was accepted.");
        }
        var tooMany = Enumerable.Repeat(first[0], 201).ToList();
        var overflowRejected = false;
        try { DiagnosticHistoryStore.BuildRedactedSupportReport(tooMany); }
        catch (InvalidDataException) { overflowRejected = true; }
        if (!overflowRejected)
            throw new InvalidOperationException("Unbounded redacted report was accepted.");
        Console.WriteLine("MYTAGSYNC_REDACTED_SUPPORT_REPORT=PASS");

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
        // Explicitly clearing the saved history is idempotent. Subsequent
        // events are independent and remain redacted.
        DiagnosticHistoryStore.Clear(path);
        DiagnosticHistoryStore.Clear(path);
        if (File.Exists(path) || DiagnosticHistoryStore.Load(path).Count != 0)
            throw new InvalidOperationException("Saved history was not erased.");
        DiagnosticHistoryStore.Append("Source verified: " + privatePath, path);
        if (DiagnosticHistoryStore.Load(path).Single().Code != "SOURCE_VERIFIED" ||
            File.ReadAllText(path).Contains("private-file", StringComparison.Ordinal))
            throw new InvalidOperationException("History did not resume privately after clear.");

        // Clear and append must serialize even when concurrent WPF instances
        // mutate the same history. No saved personal paths or invalid JSON.
        var mixed = Path.Combine(root, "mixed-clear-append.json");
        Parallel.For(0, 48, i =>
        {
            if (i % 7 == 0) DiagnosticHistoryStore.Clear(mixed);
            else DiagnosticHistoryStore.Append("Source verified: " + privatePath + i, mixed);
        });
        var mixedEntries = DiagnosticHistoryStore.Load(mixed);
        if (mixedEntries.Any(e => e.Code != "SOURCE_VERIFIED") ||
            (File.Exists(mixed) && File.ReadAllText(mixed).Contains("Someone", StringComparison.Ordinal)))
            throw new InvalidOperationException("Mixed clear/append corrupted or leaked history.");

        // Multiple WPF processes/threads must not lose read-modify-write events.
        var concurrent = Path.Combine(root, "parallel.json");
        Parallel.For(0, 32, i =>
            DiagnosticHistoryStore.Append("Source verified: " + privatePath + i, concurrent));
        var concurrentEvents = DiagnosticHistoryStore.Load(concurrent);
        if (concurrentEvents.Count != 32 ||
            concurrentEvents.Any(e => e.Code != "SOURCE_VERIFIED") ||
            File.ReadAllText(concurrent).Contains("Someone", StringComparison.Ordinal))
            throw new InvalidOperationException("Concurrent redacted history lost or leaked events.");

        // Reject a linked ancestor even when the final history file is new.
        // Symlink creation can require privilege on some Windows runners.
        var actual = Path.Combine(root, "real-parent");
        var linked = Path.Combine(root, "linked-parent");
        Directory.CreateDirectory(actual);
        try { Directory.CreateSymbolicLink(linked, actual); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
        if (Directory.Exists(linked))
        {
            var refused = false;
            try { DiagnosticHistoryStore.Append("Settings loaded.", Path.Combine(linked, "child", "history.json")); }
            catch (InvalidDataException) { refused = true; }
            if (!refused || Directory.Exists(Path.Combine(actual, "child")))
                throw new InvalidOperationException("Linked ancestor bypassed history write guard.");
            var clearRefused = false;
            try { DiagnosticHistoryStore.Clear(Path.Combine(linked, "child", "history.json")); }
            catch (InvalidDataException) { clearRefused = true; }
            if (!clearRefused || Directory.Exists(Path.Combine(actual, "child")))
                throw new InvalidOperationException("Linked ancestor bypassed history clear guard.");
            Console.WriteLine("MYTAGSYNC_DIAGNOSTIC_HISTORY_LINK_GUARD=PASS");
        }
        else Console.WriteLine("MYTAGSYNC_DIAGNOSTIC_HISTORY_LINK_GUARD=NOT_TESTED_PRIVILEGE");

        // Existing symbolic file links must never be deleted or followed.
        var unrelated = Path.Combine(root, "unrelated.txt");
        var fileLink = Path.Combine(root, "history-link.json");
        File.WriteAllText(unrelated, "Unrelated file unchanged");
        try { File.CreateSymbolicLink(fileLink, unrelated); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
        if (File.Exists(fileLink))
        {
            var clearRefused = false;
            try { DiagnosticHistoryStore.Clear(fileLink); }
            catch (InvalidDataException) { clearRefused = true; }
            if (!clearRefused || File.ReadAllText(unrelated) != "Unrelated file unchanged")
                throw new InvalidOperationException("History clear followed another file's link.");
        }
        Console.WriteLine("MYTAGSYNC_DIAGNOSTIC_HISTORY_CLEAR=PASS");
        Console.WriteLine("MYTAGSYNC_DIAGNOSTIC_HISTORY_CONCURRENCY=PASS");
        Console.WriteLine("MYTAGSYNC_REDACTED_DIAGNOSTIC_HISTORY=PASS");
    }
}
