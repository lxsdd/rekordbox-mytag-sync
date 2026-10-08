# Repository operating rules

This repository is the canonical source of truth for `rekordbox-mytag-sync`. Development, automated builds, qualification artifacts, and releases must remain reproducible from GitHub without a persistent local development checkout.

## Product safety boundaries

- foobar2000 audio files, tags, private databases, and bridge snapshots are read-only inputs.
- The application may write only to the explicitly selected supported rekordbox `master.db` after a successful preview and all safety gates pass.
- Fail closed when the rekordbox database/version is unsupported or ambiguous, rekordbox is running, bridge/source identity is inconsistent, path canonicalization is unsafe, or a required backup cannot be created and validated.
- Preserve manual rekordbox My Tags. Remove only stale Track↔MyTag assignments whose provenance proves they were created by this tool.
- MyTag definitions and assignments must remain idempotent and duplicate-free.
- Keep exactly one rolling validated pre-write backup per distinct rekordbox database/library; no backup history.
- A database mutation must be transactional and followed by integrity validation; restore is allowed only for the matching database identity with rekordbox closed.

## SQLCipher and mutation boundary

- Keep SQLCipher connection/key handling structurally separate from mutation SQL and mutation self-tests. `RekordboxSqlCipherDatabase` owns connection-string/cipher setup; `RekordboxMutationSession` opens the guarded read-write connection; mutation helpers/executors operate on an already-open connection/session and must not duplicate key-handling code.
- Keep deterministic mutation tests in key-free fixtures whenever encryption itself is not the subject under test. Retain the encrypted fixture only for encryption/schema/open-path qualification and end-to-end tests that genuinely require it.
- Do not introduce credential-like hard-coded test literals. Derive deterministic synthetic fixture material from neutral non-secret labels when encryption tests require key material.
- Do not commit the rekordbox vendor database key in plaintext or reversible obfuscated form. Automatic access may consume a previously cached local candidate or a pinned public compatibility source, but every candidate must be verified against the explicitly selected local database before use.
- Database key material is session-only product state: never log it, never store it in application settings or qualification artifacts, and never expose it through diagnostics. External compatibility sources must be immutable commit-pinned URLs and their payloads must not be persisted.
- Production DBVersion compatibility is runtime schema-qualified only after supported rekordbox 6/7 installation evidence and the full required database schema have been validated. Legacy user-entered DBVersion allowlists must not influence production access.
- GitHub Contents updates replace whole files. Therefore avoid coupling key-bearing fixture/connection code and frequently changing mutation logic in the same source file; this reduces false-positive safety classification and is also the required separation-of-concerns design.
- A payload-specific repository write denial is not evidence of lost GitHub access. Verify a neutral Issue/Contents write, then continue through the logically correct payload-isolated file boundary. Never obfuscate payloads, split strings to evade filtering, or use low-level blob/tree/commit workarounds.

## Bridge and path inputs

- Consume the profile-local `foo_dj_library_bridge` contract and preserve `(path, subsong)` identity.
- Support automatic and manual bridge-source selection analogous to DJ Library.
- Resolve Windows canonical paths and SUBST mappings conservatively. Never silently treat a performance/external SSD path as the local canonical music source.
- Support standard bridge fields and arbitrary additional foobar metadata from the schema-versioned single canonical bridge snapshot without redundant duplicate storage of core fields.

## Mapping semantics

- Support configurable arbitrary foobar-tag → rekordbox-MyTag mappings including multivalue fields.
- Required transformations include direct value, year extraction from DATE, per-value mapping, regex/replace, prefix, and ignore-empty.
- Preview must report additions, removals, already-correct assignments, conflicts, and unmatched tracks before any write is enabled.

## Build and CI

- GitHub Actions is the build authority.
- Follow the shared `lxsdd/dev-infrastructure/STANDARDS/CI-QUALIFICATION.md` tier model used by DJ Library.
- Normal push/PR runs are the FAST gate and must not create long-lived release candidates.
- A manually dispatched FULL candidate build must produce one immutable SHA-bound portable Windows artifact with integrity metadata.
- Product self-tests must be deterministic and fail closed when expected safety assertions are missing.
- Visible/interactive WPF behavior follows `lxsdd/dev-infrastructure/STANDARDS/UI-RUNTIME-QUALIFICATION.md` and the same direct-production-path principles used by DJ Library.

## Candidate and release policy

- The first candidate intended for user qualification must contain the complete agreed product scope; do not ship a reduced Genre/Year-only product stage.
- Real Windows validation must use the exact immutable FULL candidate artifact.
- No final release/tag until the exact candidate has passed the required live rekordbox database and RX3/runtime acceptance.
- Release promotion must reuse and verify the qualified candidate; never rebuild after runtime qualification.

## Change isolation

- Do not modify `lxsdd/dj-library` or `lxsdd/foo_dj_library_bridge` except for deliberate coordinated compatibility work required by this product.
- Preserve DJ Library's existing matching, strong-only GENRE projection, source handling, and read-only bridge safety boundaries.
- Document any bridge schema/persistence boundary change and qualify compatibility across all affected repositories.
