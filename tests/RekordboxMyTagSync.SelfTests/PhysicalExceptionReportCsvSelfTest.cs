using System.IO;
using System.Text;
using RekordboxMyTagSync.Core;

public static class PhysicalExceptionReportCsvSelfTest
{
    public static void Run(string root)
    {
        var dir = Path.Combine(root, "physical-exception-report");
        Directory.CreateDirectory(dir);
        var sentinel = Path.Combine(dir, "untouched.bin");
        File.WriteAllBytes(sentinel, [0x11, 0x22, 0x33, 0x44]);
        var before = File.ReadAllBytes(sentinel);
        var row = new PhysicalExceptionRow(
            "SAME_REKORDBOX_PATH", "42", @"R:\Music\Duplicate.mp3", "42, 43",
            @"Z:\Music\Duplicate.mp3", "Disk present", "One physical foobar row",
            "2", "0", "3", "1", "Not verifiable", "=1+1");
        var csv = PhysicalExceptionReportCsv.Render([row]);
        if (!csv.StartsWith("\"Reason\",\"Content ID\",\"rekordbox path\"\r\n",
                StringComparison.Ordinal) ||
            !csv.Contains("\"42, 43\"", StringComparison.Ordinal) ||
            !csv.Contains("\"'=1+1\"", StringComparison.Ordinal))
            throw new InvalidOperationException("CSV headers, quoting or formula neutralization failed.");
        var path = Path.Combine(dir, "exceptions.csv");
        PhysicalExceptionReportCsv.WriteAtomic(path, [row]);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB ||
            bytes[2] != 0xBF || File.ReadAllText(path, Encoding.UTF8) != csv)
            throw new InvalidOperationException("CSV atomic UTF-8 export failed.");
        PhysicalExceptionReportCsv.WriteAtomic(path, [row]);
        if (!bytes.SequenceEqual(File.ReadAllBytes(path)))
            throw new InvalidOperationException("Repeat CSV export is not idempotent.");
        var rejected = false;
        try { PhysicalExceptionReportCsv.WriteAtomic(sentinel, [row]); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected || !before.SequenceEqual(File.ReadAllBytes(sentinel)))
            throw new InvalidOperationException("Non-CSV destination accepted or unrelated file modified.");
        rejected = false;
        try { _ = PhysicalExceptionReportCsv.Render(Enumerable.Repeat(row, 101).ToArray()); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Oversized CSV accepted.");
    }
}
