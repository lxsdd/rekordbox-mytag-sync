# MyTagSync distribution rights and candidate handoff — fail-closed

## Current status (2026-10-20)

- A real, deterministic portable `win-x64` package is created **only inside the temporary Windows GitHub Actions runner**, with manifest and ZIP SHA verified on exact source SHA.
- On the **public repository**, `.github/workflows/full.yml` has the candidate `actions/upload-artifact` step deliberately disabled, and `.github/workflows/promote-release.yml` has its release job disabled. `scripts/verify-public-artifact-hold.py` checks these guards during FAST. **Do not remove or bypass them from a documentation-only update.**
- No publicly downloadable candidate should be inferred from a green FULL run. An unencrypted artifact uploaded to a public repository must be treated as publicly accessible to other people with repository read access.

## Verified package dependency metadata (locked production inputs)

These are the locked NuGet dependencies from `src/RekordboxMyTagSync/packages.lock.json`, **not an assertion that every runtime/redistribution obligation is discharged**.

| Package | Locked version | Declared license | Primary evidence |
| --- | --- | --- | --- |
| `Microsoft.Data.Sqlite.Core` | 10.0.13 | MIT | https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/10.0.13 |
| `Microsoft.NET.ILLink.Tasks` | 10.0.13 | MIT | https://www.nuget.org/packages/Microsoft.NET.ILLink.Tasks/10.0.13 |
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

## October 2026 .NET servicing and Windows SDK licensing gate

Official .NET 10.0.13 servicing release (2026-10-13) fixes multiple security
issues and ships in SDK **10.0.402** / **10.0.113**:
https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.13/10.0.13.md

The FAST and FULL Windows CI build definitions now **request SDK 10.0.402**.
A separate, synthetic-negative-tested guard verifies both
`Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App` at least
**10.0.13**. FULL also explicitly requests `RuntimeFrameworkVersion=10.0.13`
for *both* self-contained win-x64 publishes. Passing this guard is proof of
the build environment and publish request, **not** independent proof of the
contents of the bundled single-file EXE. The final manifest, executable and
source SHA still require a separate artifact-specific audit before delivery.

Microsoft's specific .NET-on-Windows licensing inventory says
`coreclr.dll`/single-file embedded .NET runtimes,
`PresentationNative_cor3.dll`, `vcruntime140_cor3.dll` and
`wpfgfx_cor3.dll` are subject to **.NET Library License**; additionally,
`D3DCompiler_47_cor3.dll` is governed by the **Windows SDK License**.
Everything else is not automatically covered by one blanket MIT attribution:
https://github.com/dotnet/core/blob/main/license-information-windows.md
and https://learn.microsoft.com/en-us/legal/windows-sdk/license

Before permitting any binary handoff, account for any applicable Windows SDK
distributable rights and third-party notices **as well as** runtime license
notices. Do not assume the earlier NuGet license-audit.tsv addresses native
single-file payloads. The public artifact and release gates remain disabled.

**NuGet lock status:** `Microsoft.Data.Sqlite.Core` and SDK-derived
`Microsoft.NET.ILLink.Tasks` were updated to **10.0.13** using `dotnet restore
--force-evaluate` on an actual Windows SDK 10.0.402 runner. Both committed
product and self-test `packages.lock.json` files contain NuGet-generated
SHA-512 `contentHash` values. Ordinary FAST/FULL must still prove the
result with strict `--locked-mode` before qualification or merger.
Distribution remains on HOLD pending the complete license/notice audit.

## Actual single-file embedded component inventory (still NOT distribution approval)

The Windows FULL workflow now reads the **actual** .NET single-file header
and embedded file-entry manifest from **both** independently published EXEs,
rather than inferring embedded runtime/native dependencies from NuGet metadata
or the outer ZIP alone. The parser follows Microsoft's documented
`Microsoft.NET.HostModel.Bundle.Manifest` / `FileEntry` format, rejects
wrong/duplicate/unsafe bundle entries and truncated/out-of-range payload
offsets, and positively requires `System.Private.CoreLib.dll` as a core managed runtime
identity (native `coreclr.dll` may be prelinked in modern single-file builds),
at least one real WPF
managed/native component and native SQLite3MC **across both the embedded
manifest and any separate published companions**. This explicitly avoids
the previously overstrict assumption that native `wpfgfx_cor3.dll`
must always be embedded rather than shipped alongside the EXE. FAST
now performs an actual, disposable PR-only self-contained Windows
publish and runs the same audit before any SHA-bound merge, in addition
to synthetic negative tests.

The sanitized `embedded-bundle-inventory.json` lists every embedded member,
reported size, bundle type and **license-review category** without source
filesystem paths or passwords. FULL compares both embedded inventories,
includes the inventory in the deterministic candidate ZIP, and still
**never uploads** that ZIP. The inventory is a concrete evidence source
for subsequent runtime and Windows SDK NOTICE coverage. It **does not**
contain verified full license notices, determine copyright ownership of
every member, or by itself permit distribution. Review all
`UNCLASSIFIED_REQUIRES_REVIEW` rows and license requirements before
any private delivery; preserve the disabled upload/release gates.

## Actual embedded payload checksums (technical identity, not license clearance)

A follow-up to the actual WPF/SQLCipher inventory now SHA-256-hashes the
**exact bytes stored inside the sealed .NET single-file EXE** for every
embedded entry, in addition to the pre-existing outer executable SHA-256
and SHA-256 of every external publish companion. The inventory records
`onDiskBytes`, `onDiskEncoding` (`RAW` or `COMPRESSED`) and
`onDiskSha256`, sorted by relative name. Compressed entries are hashed in
their stored representation; this is deliberately **not** a claim to have
decompressed each managed assembly or traced it to its upstream SDK pack.
Overlapping member payload offsets are fail-closed. Synthetic byte mutation
must change that member digest, and a synthetic overlapping region must
be rejected. Both separately published portable builds must produce
**byte-for-byte identical inventories**, not just identical EXE digests.

This enables an eventual component-by-component notice provenance comparison.
It does **not** provide verified source copyright notices, attest an
upstream signature, clear Microsoft's .NET or Windows SDK redistribution
conditions, authorize a public artifact, or qualify a delivery to the user.
The blocking license status remains `REVIEW_REQUIRED_NOT_AUTHORIZED`.

## Narrowed WindowsDesktop/SQLitePCLRaw attribution leads (not license approval)

The actual sealed 10.0.13 self-contained Windows bundle enumerated **388
embedded members and zero sidecars**, including 145 previously unclassified
names. The exact-name evidence has now been traced to a bounded classification
table: 13 language directories with WPF UI Automation/Presentation satellite
assemblies, individual WPF themes/core assemblies and input dependencies,
`mscorlib.dll`/`netstandard.dll`, and the **two** locked
`SQLitePCLRaw` managed NuGet assemblies. Each is labeled only with an
`*_ATTRIBUTION_PENDING` source-pack *lead*, not a verified license.

The Python inventory tests explicitly reject unknown vendor assemblies and
unknown language/resource combinations as `UNCLASSIFIED_REQUIRES_REVIEW`.
Only exact enumerated names from this observed bundle receive hints, so
future runtime updates introduce visible review requirements instead of
silently inheriting an assumed copyright owner. The full on-disk member
SHA-256s remain required to verify these leads against original SDK/runtime/
NuGet source bytes. Microsoft's own Windows license inventory distinguishes
its WindowsDesktop native exceptions; package hints do not replace complete
corresponding copyright, MIT/Apache, .NET Library License and Windows SDK
notices, nor do they authorize any redistribution or remove the private
handoff constraint. Keep `REVIEW_REQUIRED_NOT_AUTHORIZED` unchanged.

## Pinned source-pack byte provenance (evidence, not attribution approval)

Windows FULL now additionally compares each **uncompressed (RAW)** embedded or
published companion's actual SHA-256 **and its filename and byte count** to
files in specifically version-pinned .NET 10.0.13 Windows runtime pack roots
and locked SQLite3MC/SQLitePCLRaw/Microsoft.Data.Sqlite NuGet roots. This
does not rely on filename similarity alone. Source matches identify the
source-package label and source-relative path, without leaking runner-local
absolute paths. The package identity and exact restored bytes still need their
own license and notice review.

The `sourceByteEvidence` inventory extension separates
`MATCHED_EXACT_RAW_BYTES`, `UNMATCHED_REQUIRES_REVIEW` and
`COMPRESSED_UNRESOLVED`; compressed bundle bytes are **not** treated as
matching their uncompressed source automatically. Entire source categories
may remain unmatched when their runtime packs are not present on the runner,
and byte matches do not independently attest upstream package signatures,
license permissions, assembly authorship or compliance with notices. Tests
reject invalid/duplicate source labels, absent roots and misleading same-name
or changed bytes. FULL compares this complete evidence from both independently
published Windows candidates byte for byte, and still leaves
`distributionLicenseStatus=REVIEW_REQUIRED_NOT_AUTHORIZED`. This is a
technical origin-correlation milestone, **not a redistributable release gate**.

## Exact license/ThirdPartyNotice source-byte preservation — still incomplete

The Windows FULL runner additionally requires the installed .NET SDK's
`LICENSE.txt` and `ThirdPartyNotices.txt` to exist and preserves their **exact
original bytes**, file lengths and SHA-256 checksums. If explicit text files
exist in pinned .NET 10.0.13 runtime-pack or locked SQLite3MC/SQLitePCLRaw
NuGet package folders, those are also preserved, but absent optional files
remain unresolved rather than being invented from short SPDX metadata.
Negative tests reject absent, duplicated, oversized, symlinked, non-UTF-8 or
binary sources, and refuse to overwrite previous collection output.

The temporary deterministically checked ZIP contains
`notice-source-manifest.json` and a folder named
`NOTICE-SOURCES-UNREVIEWED` with the source texts. This name is deliberate:
the texts are evidence awaiting file-by-file applicability and rights review,
**not** a complete, final third-party-notice package. In particular, an
installed .NET SDK may include notices for components that are not in this
application while failing to cover distinct SQLite3MC/SQLitePCLRaw/Windows SDK
obligations. Passing this step is only source preservation. The manifest
states `completeness=NOT_ESTABLISHED` and
`distributionLicenseStatus=REVIEW_REQUIRED_NOT_AUTHORIZED`.

A future separately verified candidate must replace this preliminary source
collection with an accurate, complete and properly displayed/distributed
license/notice set, including applicable Microsoft .NET Library License,
Windows SDK license, MIT and Apache notices. **Do not lift the public
upload/release or production database write holds merely because these
preliminary files are present.**

## Exact unresolved sealed-bundle member diagnostics

The real Windows FULL byte-origin audit now prints a small sanitized
`MYTAGSYNC_SOURCE_BYTE_UNRESOLVED_COMPONENT` record for every **actual**
bundle file whose stored bytes cannot yet be matched exactly to the
version-pinned .NET/WindowsDesktop or locked NuGet inputs, including its
source status and still-pending license-review category. This is limited to
single-file manifest **relative member names**; no local source-directory or
personal music paths are printed. The validator counts every embedded/loose
member and fails closed if the origin result omits one. The source search now
also checks the explicitly locked `SQLite3MC.PCLRaw.bundle 2.4.0` package,
in addition to its lib/provider packages. Synthetic tests require unmatched
items to retain their review classifications.

Source correlation is necessarily different from compliant redistribution.
For example, the tool's own generated assembly or .NET runtimeconfig may
remain unmatched to an external NuGet package and should instead be assigned
to an accurately supported application/build-output category. Never silently
relabel an unknown **native or third-party binary** as first-party to make
the match count 100%. The actual unaccounted-for names from Windows FULL
must determine the next specific fix; all permissions and notice obligations
remain under review and the public distribution gate stays disabled.

## Exact provenance result from real Windows FULL (2026-10-21)

The fully sealed, deterministic Windows publish at source
`9350edac932763b479ef4195c595851600d5fdce`
reported **388 embedded, zero sidecars, 385 exact raw external-package
matches, zero compressed files**. The only remaining three package-
unmatched entries are `rekordbox MyTagSync.dll`,
`rekordbox MyTagSync.deps.json`, and
`rekordbox MyTagSync.runtimeconfig.json` — the application assembly
and build-generated app metadata. This resolves the earlier question
whether a foreign native library remained outside source attribution.
It **does not** validate the intellectual-property provenance or
distribution notices for those three application outputs.

A new fail-closed attestation requires these **exact three names and
expected app-output categories** in the embedded bundle, preserves their
per-file sealed SHA-256, verifies all other embedded components have
an exact approved-source-*candidate* byte match, and rejects any new
unmatched native/third-party assembly, incomplete inventory, changed
ownership label or unexpected compressed app entry. The output status
is deliberately `APPLICATION_OUTPUT_IDENTITY_REVIEW_REQUIRED`.
That word “identity” is important: package-byte equality and
deterministic build do **not** by themselves convey license rights,
copyright ownership or a signed release.

Public binaries, candidate upload, automated promotion, and real-user
database modifications remain prohibited pending the complete terms,
copyright notices, and permitted private handoff.

## What is still needed before *any* candidate publication

1. **Published file-by-file component manifest.** Identify the *actual* files in the sealed self-contained ZIP, including transitive assemblies, native `sqlite3mc` code and self-contained .NET/WindowsDesktop runtime files. A NuGet package list alone is insufficient to establish the exact contents.
2. **Notices and terms.** Collect and retain the required full MIT and Apache-2.0 copyright/license notices (and any upstream `NOTICE` files or additional native runtime notices) **with the redistributed bundle**. The current `license-audit.tsv` lists package metadata; it is **not itself** the complete required license text.
3. **App-origin and platform boundaries.** Confirm origin/permission for any redistributable first-party compiled app assets; do not ship Pioneer/AlphaTheta/rekordbox program files, vendor database keys, user SQLCipher databases, protected test fixtures, foobar user configuration, DRM material, or any private tracks/paths. An application that can locally validate an existing user's database is not a grant to distribute that vendor's content.
4. **Safe one-candidate handoff.** Choose a destination where only the intended user can obtain the exact signed/hash-verified candidate. A normal **public** GitHub Actions artifact or public GitHub release does not provide a private handoff. No new paid storage or private-runner assumptions. Until a permitted private delivery method and distribution/notice checks are actually proven, keep candidate upload and release promotion disabled.
5. **Runtime acceptance on user's Windows 11 x64 laptop** from [Windows 11 read-only acceptance](WINDOWS11-READONLY-ACCEPTANCE.md). A green Windows Server Actions job does not replace this. Separate user consent is needed before any `Apply`/`Restore` to a real `master.db`.

## Release gate

Any future change to the two disabled upload/promotion gates must be a separately reviewed, SHA-bound PR demonstrating (1) actual bundle contents and notices, (2) no secrets/vendor binaries, (3) a cost-free permitted handoff path and storage limits, (4) successful FAST/FULL and (5) the appropriate explicit user approval. **This document does not authorize relaxing the hold**, nor does dependency MIT licensing alone make the complete package or its delivery safe.

Historical FULL software candidate: `71939b9d91258f6bb40ef7509e7744a8c7c8d5e7`; ZIP inner SHA-256 `bb82d68ff36300efee274017ac09af347c2e43843d53bc6199c6b43a860d2cf0`; no published artifact.
