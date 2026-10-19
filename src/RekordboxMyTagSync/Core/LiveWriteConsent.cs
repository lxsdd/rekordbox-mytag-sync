using System.Security.Cryptography;
using System.Text;

namespace RekordboxMyTagSync.Core;

// Human-typed, one-operation local consent. Never persist this phrase or
// interpret a Preview, a test run, or a backup as user authorization.
public enum LiveWriteOperation { Apply, Restore }

public static class LiveWriteConsent
{
    public static string Challenge(
        LiveWriteOperation operation,
        RekordboxDatabaseIdentity identity,
        string approvedScopeProof)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Enum.IsDefined(operation) ||
            string.IsNullOrWhiteSpace(identity.DbId) ||
            string.IsNullOrWhiteSpace(identity.CanonicalPath) ||
            string.IsNullOrWhiteSpace(approvedScopeProof))
            throw new ArgumentException("Live write consent requires a verified operation, database and scope.");

        // PreviewIdentity binds the physical canonical path, DBID, DBVersion,
        // file size and modification time. Scope binds either a freshly
        // approved preview fingerprint or the validated backup preimage SHA.
        var material = string.Join("\n", operation, identity.PreviewIdentity, approvedScopeProof);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return $"{operation.ToString().ToUpperInvariant()} {identity.DbId} {hash[..12]}";
    }

    public static bool Matches(
        LiveWriteOperation operation,
        RekordboxDatabaseIdentity identity,
        string approvedScopeProof,
        string? typedValue)
    {
        // Deliberately exact, ordinal, no trimming or case-folding. Merely
        // seeing this code in the UI never authorizes a mutation.
        return !string.IsNullOrEmpty(typedValue) &&
            string.Equals(typedValue,
                Challenge(operation, identity, approvedScopeProof),
                StringComparison.Ordinal);
    }
}
