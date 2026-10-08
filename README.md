# Rekordbox MyTag Sync

Portable Windows companion for synchronizing arbitrary foobar2000 metadata from `foo_dj_library_bridge` into rekordbox 6/7 My Tags.

Safety: bridge/audio/tag inputs are read-only; every write requires a fresh preview and closed rekordbox; exactly one validated rolling pre-write backup is kept per distinct rekordbox DBID; writes are transactional, idempotent and duplicate-free; manual My Tags are never blindly removed; only assignments recorded in tool provenance may be removed as stale; unsupported or ambiguous database states fail closed.

Database access is qualified automatically. The application first accepts a locally available compatibility candidate when present, otherwise it can retrieve a candidate from immutable commit-pinned public compatibility sources. The candidate is never trusted directly: it must successfully open the explicitly selected local `master.db`, after which the required rekordbox schema and DB identity are validated. The vendor database key is not committed to this repository, stored in application settings, emitted to logs, or included in qualification artifacts. If no local candidate exists, internet access may therefore be required for the first access qualification in an application session.

rekordbox 6/7 installations and active library paths are discovered automatically, with a manual `master.db` Browse fallback. DBVersion is recorded from the database but production compatibility is established from supported rekordbox 6/7 installation evidence plus the required runtime schema; legacy user-entered DBVersion allowlists are ignored.

GitHub Actions is the build authority. FAST CI qualifies normal commits using locked dependencies. FULL candidate qualification performs deterministic self-tests, public-repository hygiene/history checks, dependency/vulnerability/license audits, live compatibility-source availability checks, WPF UI smoke, repeated deterministic publish/package verification, and produces one immutable self-contained Windows candidate with Source SHA and Artifact SHA-256.

Release promotion reuses and verifies that exact FULL artifact without rebuilding it. No final tag/release is permitted before the exact candidate has passed the required live rekordbox/RX3 acceptance.
