# Rekordbox MyTag Sync

Portable Windows companion for synchronizing arbitrary foobar2000 metadata from `foo_dj_library_bridge` into rekordbox 6/7 My Tags.

Windows cross-version compatibility: FULL now also tests the exact source SHA on the standard Windows Server 2022 runner, alongside the existing Windows 2025 runtime. It runs locked dependency restore, the full encrypted SQLCipher/native alias fixture suite, real WPF startup, single-file self-contained win-x64 publication and published WPF smoke with **zero artifact upload**. This is an older Windows API witness, NOT proof of client Windows 10 Home/Pro support. Real Windows10/rekordbox/RX3 acceptance is still mandatory. After successful SQLCipher validation the WPF Target status displays the canonical physical master.db and calls out an aliased selected path; a copied physical database cannot be labeled as that target (positive/negative UI smoke).

Safety: bridge/audio/tag inputs are read-only; every write requires a fresh preview and closed rekordbox; exactly one validated rolling pre-write backup is kept per distinct rekordbox DBID; writes are transactional, idempotent and duplicate-free; manual My Tags are never blindly removed; only assignments recorded in tool provenance may be removed as stale; unsupported or ambiguous database states fail closed.

Bridge V3 canonical aliases and bounded parsing: producer synonyms YEAR/DATE and PUBLISHER/LABEL now preserve true Core multivalues in the existing canonical columns. Duplicate vector aliases converging to one field (e.g. TRACK/TRACKNUMBER) are rejected rather than silently overwritten. A compressed snapshot advertising 0 or fewer rows than it contains stops parsing at the first over-limit row. Positive/negative synthetic V3 inputs and the production WPF DATE-to-Year smoke verify this behavior; legacy V1/V2 remain unchanged.

Bridge atomic publication guard: Source discovery compares state schema_version with the compressed snapshot header without inflating 52k rows. ReadStable parses a single pinned compressed snapshot and rejects payload/state version mismatches (24/25/26 columns), count mismatches, incomplete generations, changed bridge-state bytes and changed compressed snapshot bytes. A synthetic encrypted SQLCipher V3 full-multivalue Preview/Apply/idempotence fixture, malformed legacy-vs-V3 publication tests and real WPF Source discovery smoke qualify this fail-closed behavior. No live database, tag or media writes are enabled.

Bridge V3 compatibility: full foobar Core multivalues (notably GENRE, DATE, ARTIST) are read from the producer's metadata_vectors_json while legacy 24/25-column V1/V2 records retain their original scalar behaviour. Non-Core metadata remains sourced exclusively from canonical extra_metadata_json; no redundant tag model or media writes are introduced. Malformed vectors, duplicate names ignoring case, and non-string vector values are rejected before preview. Positive tests verify GENRE→Genre and DATE→Year preserve every source value. This is read-only metadata parsing and never authorizes master.db mutation.

Configurable mapping filters: each foobar tag mapping can optionally include a time-bounded
source-value regex filter. Matching source values are included by default; "Exclude matches"
inverts the selection. Filtering is performed on each original value before optional
multivalue joining, regex replacement, year extraction and prefixing. No matching values
means no proposed MyTag assignment, even when Ignore empty is disabled. The filter
pattern and inversion are saved in settings and included in the preview fingerprint,
so changing a filter invalidates an older preview. Existing mappings without filters
retain their previous behavior. The WPF Mapping tab exposes both controls; malformed
patterns fail closed at settings validation and during preview.

Encrypted source-filter E2E: production Preview/Apply is tested against temporary encrypted SQLCipher files with two multivalue tracks and existing manual MyTags. Changing include to exclude rejects stale preview and preserves database, provenance and backup bytes; fresh approval adds/removes only tool-owned assignments, rolls exactly one prewrite backup forward, and repeated Apply is byte-for-byte idempotent. Actual WPF regex-text and exclude-checkbox bindings edit the production mapping row; a UI-smoke-only settings file is saved/reloaded and Preview fingerprints are rechecked. These are synthetic tests, not permission for live writes.

Multiple Windows/SUBST/UNC path aliases participate in a single read-only verified Preview. Pair uniqueness is evaluated across all roots; duplicate source files and duplicate rekordbox paths remain excluded. The remaining candidates must pass native VolumeSerial/FileIndex equality; copied files are rejected. The read-only observation cache is bound to the entire alias set. Synthetic encrypted SQLCipher metadata, Windows hardlink/copy, virtual CUE and collision fixtures exercise this production path, alongside the WPF alias smoke. No alias or file-name match is a write authorization.

Exact SQLCipher preview-byte authority: creating a physically approved preview hashes the selected encrypted master.db once and binds its SHA-256 to the immutable native-file scope. Production Apply compares its already computed prewrite SHA against that captured approval BEFORE provenance/rolling backup, detecting external edits even if DBID, schema, length and LastWriteTimeUtc are preserved. A synthetic encrypted negative E2E deliberately updates a non-MyTag SQLCipher counter then restores the original timestamp and verifies rejection without any backup, provenance or DB write. The WPF startup smoke checks target changes revoke approved mapping/scope state. Automatic FAST-triggered FULL still performs all tests without uploading a large intermediate package.

Database alias identity: for existing master.db files, native read-only Windows GetFinalPathNameByHandle resolves drive letters, SUBST and junctions to their actual shared file path. Discovery, SQLCipher snapshots, rolling prewrite backup slots, restore and minimal tool-owned provenance use that path; distinct copied databases with identical DBID remain separate. A master.db with more than one hardlink is rejected, because an atomic file replacement would split the paths. Missing database candidates remain unsafe. Windows encrypted SQLCipher E2E exercises synthetic junction aliases with two rekordbox configuration sources, rolling backup reuse, provenance identity, copied-DB separation and hardlink rejection. Live Apply/Restore remain disabled without independent user permission.

Physically scoped Apply preparation: the read-only verified WPF preview now retains a typed, immutable ContentID/file-pair authority together with the actual (including automatically suggested) mapping set. The underlying production preflight accepts only the exact originally approved physical ContentIDs; it reruns native Windows VolumeSerial/FileIndex checks against the current full bridge and SQLCipher snapshot before any rolling backup, again after backup, and once immediately before the read-write session. Mapping, source, provenance, schema and DB identity drift remain fail-closed. Virtual CUE segments, copied files and excluded ContentIDs never authorize assignments. Synthetic encrypted positive and negative end-to-end fixtures and WPF smoke qualify this route. The UI Apply/Restore controls remain HARD-DISABLED: no real user master.db change is permitted until separate explicit approval and final live qualification.

Library-scale encrypted read-only acceptance: a synthetic SQLCipher fixture with exactly 52,784 real rekordbox ContentIDs is read on the same production snapshot path. Its foobar source includes 670 virtual-only CUE records, six duplicate-path ContentIDs and two additional absent bridge rows; only the remaining 52,106 unambiguous physical path candidates enter the read-only source/target scope. The test validates every exclusion reason, combined multi-root mapping, GENRE-to-MyTag additions, invariant encrypted database bytes and a generous bounded runtime. Native Windows FileIndex verification is tested separately with real hardlinks and copies; the large fixture's synthetic pair list never authorizes mutation. All eight real WPF DataGrid tables explicitly virtualize rows/columns with logical scrolling and remain subject to touchpad/column-persistence smoke tests.

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

Bridge V3 technical-field safety: foobar user-defined metadata named CODEC,
BITRATE or DURATION_SECONDS is permitted in source vectors, but cannot
overwrite technical codec/bitrate/duration Core columns obtained from the
producer's file info and length. Synthetic positive/negative vector fixtures
verify physical metadata retention and GENRE multivalue preservation alongside
the encrypted V3 Preview→Apply→backup→provenance→idempotence regression and
actual WPF UI smoke. This does not unlock live Apply/Restore.

Bridge V3 producer-defined alias priority: foobar fields such as DATE and
YEAR may coexist legitimately. The producer uses first-nonempty canonical
priority (DATE before YEAR, LABEL before PUBLISHER, TRACKNUMBER before TRACK
and the documented disc/catalog synonyms), independently of the sorted
metadata_vectors_json order. The reader now preserves *all values* from the
winning field and ignores empty primary vectors when a populated fallback
exists. Distinct case-insensitive duplicate field names and malformed vectors
still fail closed. Synthetic encrypted SQLCipher Preview→Apply→rolling
backup→provenance→idempotence now starts with YEAR-only V3 values; producer
priority/empty fallback fixtures and real WPF smoke run in FAST/FULL. Technical
file-info fields remain unspoofable, and live Apply/Restore remain locked.

Local physical exception CSV export: after a fresh read-only inspection, the Physical exceptions tab saves the 13 evidence fields for exceptional ContentIDs to a user-selected UTF-8 CSV. It contains personal file paths/ContentIDs; never post it publicly. Source/target/mapping changes revoke the report; export checks bridge publication, DB file metadata and rekordbox/rbxport process state. Spreadsheet formula prefixes are neutralized. Only the selected CSV is written atomically; master.db, tags, CUE, media, backups and provenance remain untouched. FAST/FULL retain synthetic encrypted SQLCipher exception tests and real WPF smoke.

One-operation local write confirmation: after a physically verified, conflict-free
Preview and runtime-qualified SQLCipher master.db, the Apply tab shows a database-,
source-preview- and operation-bound challenge. The user must type it exactly;
a code is never persisted and is consumed BEFORE touching backup/provenance or
master.db. The corresponding Restore challenge instead binds the validated
rolling backup preimage; Restore refuses a backup image changed since consent.
Changing target/Preview revokes consent. Both operations still independently
enforce closed rekordbox/rbxport, physical identity, DB/schema, backup, transaction
and postimage rules. The code is displayed only on the user's local Windows UI;
nothing is uploaded. Typed confirmation is explicit user authorization at the
local device, NOT authorization for this development process to touch any real
master.db. Synthetic encrypted SQLCipher positive/negative confirmation
regressions and actual WPF controls are exercised in FAST/FULL.

Pinned Restore preimage: the SQLCipher RestoreVerified executor receives
the exact SHA-256 of the rolling backup image the local user explicitly
confirmed. Inspection, temporary extraction and final replacement must match
that approved image; even a subsequently valid rotation of the SAME single
backup slot cannot be adopted silently. An encrypted SQLCipher positive
Restore and negative wrong-preimage E2E prove byte preservation on rejection;
the production WPF handler passes the scoped approval into the executor
rather than relying only on a preflight UI check. This introduces no
unsolicited writes and no additional retained backup history.
