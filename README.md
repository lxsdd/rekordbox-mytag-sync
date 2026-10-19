# Rekordbox MyTag Sync

Portable Windows companion for synchronizing arbitrary foobar2000 metadata from `foo_dj_library_bridge` into rekordbox 6/7 My Tags.

Safety: bridge/audio/tag inputs are read-only; every write requires a fresh preview and closed rekordbox; exactly one validated rolling pre-write backup is kept per distinct rekordbox DBID; writes are transactional, idempotent and duplicate-free; manual My Tags are never blindly removed; only assignments recorded in tool provenance may be removed as stale; unsupported or ambiguous database states fail closed.

Configurable mapping filters: each foobar tag mapping can optionally include a time-bounded
source-value regex filter. Matching source values are included by default; "Exclude matches"
inverts the selection. Filtering is performed on each original value before optional
multivalue joining, regex replacement, year extraction and prefixing. No matching values
means no proposed MyTag assignment, even when Ignore empty is disabled. The filter
pattern and inversion are saved in settings and included in the preview fingerprint,
so changing a filter invalidates an older preview. Existing mappings without filters
retain their previous behavior. The WPF Mapping tab exposes both controls; malformed
patterns fail closed at settings validation and during preview.

Physical exception diagnostics: the targeted, read-only eight-ContentID investigation reports duplicate rekordbox paths and missing bridge paths separately from virtual CUE subsongs. Optional playlist, cue, beatgrid, MyTag and hot-cue-bank tables are counted by ContentID only when their columns are directly verifiable. Deleted rows are excluded when a deletion flag exists; missing or incompatible optional tables display "Not verifiable", never an invented zero. Encrypted positive/negative regression fixtures validate beatgrid counts and that the inspected database image remains unchanged. No track is automatically removed based on reference counts.

Concurrent writers: the closed-library process guard treats `rekordbox`, `rekordboxAgent`, and `rbxport` as protected processes. `rbxport` may replace an entire track's MyTag membership set, whereas MyTagSync changes only tool-owned assignments. A synthetic encrypted SQLCipher regression verifies that external removal of a tool-owned membership invalidates saved provenance and blocks stale Apply before any backup or database/provenance write; independently edited manual memberships are not adopted or deleted. This is a fail-closed compatibility prerequisite, not proof of complete rbxport interoperability or permission to write a live library.

The verified restore entry point checks a freshly decrypted SQLCipher snapshot against the approved database identity and compares full-file SHA-256 before and after the read. Automatic rollback uses the same entry point. The restore operation independently refuses to run while rekordbox is open; a relaxed read policy in synthetic tests does not relax that process guard.

Restore also supports an approved target SHA-256 guard: if provided, changed target bytes are rejected before replacement. This guard alone does not qualify a manual restore; the caller must independently revalidate the live SQLCipher identity and maintain a closed-database write gate.

The mutation path cryptographically binds its SQLCipher snapshots to the exact pre-write database image stored in the rolling backup. SHA-256 validation rejects changed bytes even when file length and modification timestamp remain unchanged. Read-only previews do not perform this full-file hash pass. A green FAST run is required before this safety gate can be considered qualified.

Database access is qualified automatically. The application first accepts a locally available compatibility candidate when present, otherwise it can retrieve a candidate from immutable commit-pinned public compatibility sources. The candidate is never trusted directly: it must successfully open the explicitly selected local `master.db`, after which the required rekordbox schema and DB identity are validated. The vendor database key is not committed to this repository, stored in application settings, emitted to logs, or included in qualification artifacts. After successful local verification, the key may be cached only with Windows current-user DPAPI and is bound to the canonical database path. If neither that protected cache nor a compatible pyrekordbox cache exists, internet access may therefore be required for the first qualification for that Windows user/database path.

Path mapping: aliases may use a complete Windows drive root (such as Z:\ to R:\) or a UNC share root, as well as a deeper music directory. Path-root boundary checks distinguish sibling shares and directories and reject drive-relative paths. Alias equivalence is only a candidate for the separate, mandatory native VolumeSerial/FileIndex verification; it is never by itself permission to write rekordbox.

rekordbox 6/7 installations and active library paths are discovered automatically, with a manual `master.db` Browse fallback. DBVersion is recorded from the database but production compatibility is established from supported rekordbox 6/7 installation evidence plus the required runtime schema; legacy user-entered DBVersion allowlists are ignored.

GitHub Actions is the build authority. FAST CI qualifies normal commits using locked dependencies. FULL candidate qualification performs deterministic self-tests, public-repository hygiene/history checks, dependency/vulnerability/license audits, live compatibility-source availability checks, WPF UI smoke, repeated deterministic publish/package verification, and produces one immutable self-contained Windows candidate with Source SHA and Artifact SHA-256.

Release promotion reuses and verifies that exact FULL artifact without rebuilding it. No final tag/release is permitted before the exact candidate has passed the required live rekordbox/RX3 acceptance.

Preview safety: ambiguous MyTag group/value definitions produce a fail-closed Conflict rather than mutating a collection during enumeration. Managed provenance lookups use a ContentID index for large libraries; unmatched external/manual MyTag assignments remain untouched. These changes require exact-HEAD FAST and FULL qualification before promotion.
