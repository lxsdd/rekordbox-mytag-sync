using System.Security.Cryptography;
using RekordboxMyTagSync.Core;

public static class LiveWriteConsentSelfTest
{
    public static void Run(string temp)
    {
        // Read a REAL synthetic encrypted SQLCipher master.db using the
        // production reader. No actual user database and no mutation.
        var root = Path.Combine(temp, "LocalWriteConsent");
        var db = Path.Combine(root, "master.db");
        var first = Path.Combine(root, "One.mp3");
        var second = Path.Combine(root, "Two.mp3");
        EncryptedMutationFixture.Create(db, first, second);
        var beforeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(db)));
        var snapshot = RekordboxSqlCipherDatabase.ReadSnapshot(
            db, EncryptedMutationFixture.Key,
            RekordboxDatabaseReadPolicy.SchemaQualifiedRuntime(requireRekordboxClosed: false));
        var proof = new string('A', 64);
        var apply = LiveWriteConsent.Challenge(
            LiveWriteOperation.Apply, snapshot.Identity, proof);
        var restore = LiveWriteConsent.Challenge(
            LiveWriteOperation.Restore, snapshot.Identity, proof);
        if (!LiveWriteConsent.Matches(
                LiveWriteOperation.Apply, snapshot.Identity, proof, apply) ||
            !LiveWriteConsent.Matches(
                LiveWriteOperation.Restore, snapshot.Identity, proof, restore) ||
            apply == restore)
            throw new InvalidOperationException("SQLCipher-bound local consent positive check failed.");

        var differentDb = snapshot.Identity with { CanonicalPath = Path.Combine(root, "copy.db") };
        var changedImage = snapshot.Identity with { FileLength = snapshot.Identity.FileLength + 1 };
        foreach (var candidate in new[]
        {
            "", " " + apply, apply + " ", apply.ToLowerInvariant(),
            restore, "APPLY", apply + "X"
        })
        {
            if (LiveWriteConsent.Matches(
                LiveWriteOperation.Apply, snapshot.Identity, proof, candidate))
                throw new InvalidOperationException("Unsafe local consent accepted a stale/foreign phrase.");
        }
        if (LiveWriteConsent.Matches(LiveWriteOperation.Apply, differentDb, proof, apply) ||
            LiveWriteConsent.Matches(LiveWriteOperation.Apply, changedImage, proof, apply) ||
            LiveWriteConsent.Matches(LiveWriteOperation.Apply, snapshot.Identity, new string('B', 64), apply) ||
            LiveWriteConsent.Matches(LiveWriteOperation.Restore, snapshot.Identity, proof, apply))
            throw new InvalidOperationException("Local consent did not bind operation, physical DB and scope.");
        if (beforeHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(db))))
            throw new InvalidOperationException("Consent qualification modified encrypted master.db.");
    }
}
