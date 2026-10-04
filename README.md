# Rekordbox MyTag Sync

Portable Windows companion for synchronizing arbitrary foobar2000 metadata from `foo_dj_library_bridge` into rekordbox 6/7 My Tags.

Safety: bridge/audio/tag inputs are read-only; every write requires a fresh preview and closed rekordbox; exactly one validated rolling pre-write backup is kept per distinct rekordbox DBID; writes are transactional, idempotent and duplicate-free; manual My Tags are never blindly removed; only assignments recorded in tool provenance may be removed as stale; unsupported or ambiguous database states fail closed.

GitHub Actions is the build authority. FAST CI qualifies normal commits. FULL candidate qualification produces the immutable Windows package used for live acceptance; release promotion reuses that exact package without rebuilding.
