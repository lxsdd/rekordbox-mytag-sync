# MyTagSync distribution rights and candidate handoff — fail-closed

## Current status (2026-10-20)

- A real, deterministic portable `win-x64` package is created **only inside the temporary Windows GitHub Actions runner**, with manifest and ZIP SHA verified on exact source SHA.
- On the **public repository**, `.github/workflows/full.yml` has the candidate `actions/upload-artifact` step deliberately disabled, and `.github/workflows/promote-release.yml` has its release job disabled. `scripts/verify-public-artifact-hold.py` checks these guards during FAST. **Do not remove or bypass them from a documentation-only update.**
- No publicly downloadable candidate should be inferred from a green FULL run. An unencrypted artifact uploaded to a public repository must be treated as publicly accessible to other people with repository read access.

## Verified package dependency metadata (locked production inputs)

These are the locked NuGet dependencies from `src/RekordboxMyTagSync/packages.lock.json`, **not an assertion that every runtime/redistribution obligation is discharged**.

| Package | Locked version | Declared license | Primary evidence |
| --- | --- | --- | --- |
| `Microsoft.Data.Sqlite.Core` | 10.0.12 | MIT | https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/10.0.12 |
| `Microsoft.NET.ILLink.Tasks` | 10.0.12 | MIT | https://www.nuget.org/packages/Microsoft.NET.ILLink.Tasks/10.0.12 |
| `SQLite3MC.PCLRaw.bundle` | 2.4.0 | MIT | https://www.nuget.org/packages/SQLite3MC.PCLRaw.bundle/2.4.0 |
| `SQLite3MC.PCLRaw.lib` | 2.4.0 | MIT | https://www.nuget.org/packages/SQLite3MC.PCLRaw.lib/2.4.0 |
| `SQLite3MC.PCLRaw.provider` | 2.4.0 | MIT | https://www.nuget.org/packages/SQLite3MC.PCLRaw.provider/2.4.0 |
| `SQLitePCLRaw.core` | 3.0.2 | Apache-2.0 | https://www.nuget.org/packages/SQLitePCLRaw.core/3.0.2 |

The native SQLite3 Multiple Ciphers project also declares MIT (license text: https://github.com/utelle/SQLite3MultipleCiphers/blob/main/LICENSE). This is favorable evidence: neither the listed MIT packages nor `SQLitePCLRaw.core` are automatically forbidden to redistribute simply because they provide database encryption.

## Windows self-contained .NET runtime: separate licensing boundary

The NuGet package metadata in the table above does **not** cover the full
self-contained WPF executable. Microsoft's official .NET licensing guidance
states that Windows .NET *product/runtime distributions* and self-contained
apps are subject to the **Microsoft .NET Library License** and applicable
third-party notices, even where individual library NuGet packages state MIT.
The Microsoft license permits app-bound object-code distribution subject to
its distribution requirements. This is not a blanket endorsement of the
complete MyTagSync executable or its delivery mechanism.

- Official model: https://github.com/dotnet/runtime/blob/main/docs/project/licensing-assets.md
- .NET Windows licensing: https://dotnet.microsoft.com/en-us/dotnet_library_license.htm
- Redistribution overview: https://github.com/dotnet/core/blob/main/license-information.md

**Release gate:** Inspect the actual Windows runtime packs, native payloads,
license files and `ThirdPartyNotices` corresponding to the exact portable
publish output. Retain the required license/notice text in the candidate and
prove the coverage with a file-by-file audit. A passing `license-audit.tsv`
or a verified ZIP manifest only proves metadata/integrity, **not** that this
legal obligation has been met. Keep public artifact and release jobs disabled.

## What is still needed before *any* candidate publication

1. **Published file-by-file component manifest.** Identify the *actual* files in the sealed self-contained ZIP, including transitive assemblies, native `sqlite3mc` code and self-contained .NET/WindowsDesktop runtime files. A NuGet package list alone is insufficient to establish the exact contents.
2. **Notices and terms.** Collect and retain the required full MIT and Apache-2.0 copyright/license notices (and any upstream `NOTICE` files or additional native runtime notices) **with the redistributed bundle**. The current `license-audit.tsv` lists package metadata; it is **not itself** the complete required license text.
3. **App-origin and platform boundaries.** Confirm origin/permission for any redistributable first-party compiled app assets; do not ship Pioneer/AlphaTheta/rekordbox program files, vendor database keys, user SQLCipher databases, protected test fixtures, foobar user configuration, DRM material, or any private tracks/paths. An application that can locally validate an existing user's database is not a grant to distribute that vendor's content.
4. **Safe one-candidate handoff.** Choose a destination where only the intended user can obtain the exact signed/hash-verified candidate. A normal **public** GitHub Actions artifact or public GitHub release does not provide a private handoff. No new paid storage or private-runner assumptions. Until a permitted private delivery method and distribution/notice checks are actually proven, keep candidate upload and release promotion disabled.
5. **Runtime acceptance on user's Windows 11 x64 laptop** from [Windows 11 read-only acceptance](WINDOWS11-READONLY-ACCEPTANCE.md). A green Windows Server Actions job does not replace this. Separate user consent is needed before any `Apply`/`Restore` to a real `master.db`.

## Release gate

Any future change to the two disabled upload/promotion gates must be a separately reviewed, SHA-bound PR demonstrating (1) actual bundle contents and notices, (2) no secrets/vendor binaries, (3) a cost-free permitted handoff path and storage limits, (4) successful FAST/FULL and (5) the appropriate explicit user approval. **This document does not authorize relaxing the hold**, nor does dependency MIT licensing alone make the complete package or its delivery safe.

Historical FULL software candidate: `71939b9d91258f6bb40ef7509e7744a8c7c8d5e7`; ZIP inner SHA-256 `bb82d68ff36300efee274017ac09af347c2e43843d53bc6199c6b43a860d2cf0`; no published artifact.
