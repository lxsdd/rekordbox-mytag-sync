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
