using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RekordboxMyTagSync.Core;

// Independent production C# consumer of the immutable ORIGINAL C++ producer
// golden vectors. No local music, CUE, user profiles, or SQLCipher database.
public static class BridgeV3GoldenVectorsSelfTest
{
    private const string GoldenBlob = "f3b75d4608bacba23b2fd21b3350a7966e668a2e";

    public static void Run(string temp)
    {
        var fixture = Path.GetFullPath(
            Path.Combine("tests", "fixtures", "bridge-v3-cue-identity.tsv"));
        // Windows Git checkouts may apply autocrlf to a text fixture.
        // Validate the immutable Git *logical LF blob* while preserving
        // fail-closed behavior for all other content changes.
        byte[] source = Encoding.UTF8.GetBytes(
            File.ReadAllText(fixture, Encoding.UTF8).Replace("\r\n", "\n"));
        byte[] blobPrefix = Encoding.ASCII.GetBytes("blob " + source.Length + "\0");
        byte[] gitBlob = new byte[blobPrefix.Length + source.Length];
        Buffer.BlockCopy(blobPrefix, 0, gitBlob, 0, blobPrefix.Length);
        Buffer.BlockCopy(source, 0, gitBlob, blobPrefix.Length, source.Length);
        string observed = Convert.ToHexString(SHA1.HashData(gitBlob)).ToLowerInvariant();
        if (observed != GoldenBlob)
            throw new InvalidDataException(
                "Bridge golden fixture differs from pinned C++ producer Git blob.");

        var wireLines = Encoding.UTF8.GetString(source)
            .TrimEnd('\r', '\n').Split('\n');
        if (wireLines.Length != 4 ||
            wireLines.Any(line => line.Split('\t').Length != 26))
            throw new InvalidDataException("Bridge V3 CUE vectors must contain header plus 3 x 26-column rows.");

        string gzip = Path.Combine(temp, "bridge-v3-cue-golden-readonly.tsv.gz");
        WriteGzip(gzip, string.Join("\n", wireLines) + "\n");
        var tracks = BridgeSnapshot.Read(gzip);
        if (tracks.Count != 3 || tracks.Any(t => t.Path != "SYNTHETIC:/Cue Mix.flac") ||
            tracks[0].Subsong != 0 || tracks[1].Subsong != 1 || tracks[2].Subsong != 2)
            throw new InvalidDataException(
                "Actual Bridge V3 C# reader merged a physical and virtual subsong identity.");
        foreach (var t in tracks)
        {
            if (!t.GetFieldValues("DATE").SequenceEqual(new[] { "2026-10-22" }) ||
                !t.GetFieldValues("GENRE").SequenceEqual(new[] { "Trance" }) ||
                !t.GetFieldValues("MOOD").SequenceEqual(new[] { "Driving" }))
                throw new InvalidDataException(
                    "Actual Bridge V3 C# reader lost exact vector and extra provenance.");
        }

        // Important absence case: a file with only subsongs 1/2 must NEVER
        // spontaneously produce a synthetic physical subsong 0.
        WriteGzip(gzip, string.Join("\n", new[]
        {
            wireLines[0], wireLines[2], wireLines[3]
        }) + "\n");
        var virtualOnly = BridgeSnapshot.Read(gzip);
        if (virtualOnly.Count != 2 || virtualOnly.Any(t => t.Subsong == 0))
            throw new InvalidDataException("Virtual CUE-only snapshot created a physical MyTag candidate.");

        // Identical path AND subsong is a hard duplicate (not a multivalue).
        WriteGzip(gzip, string.Join("\n", new[]
        {
            wireLines[0], wireLines[2], wireLines[2]
        }) + "\n");
        Reject(gzip, "duplicate path/subsong");

        // State V3 must not be accepted with a truncated/legacy wire header.
        WriteGzip(gzip, string.Join("\n", new[]
        {
            string.Join("\t", wireLines[0].Split('\t').Take(25)),
            wireLines[1]
        }) + "\n");
        Reject(gzip, "truncated V3 schema");

        Console.WriteLine("BRIDGE_V3_CUE_GOLDEN_ORIGINAL_CPP_TO_MYTAG_CSHARP=PASS");
    }

    private static void WriteGzip(string path, string content)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        byte[] payload = Encoding.UTF8.GetBytes(content);
        gzip.Write(payload);
    }

    private static void Reject(string gzipPath, string label)
    {
        try { _ = BridgeSnapshot.Read(gzipPath); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("Bridge read accepted unsafe golden mutation: " + label);
    }
}
