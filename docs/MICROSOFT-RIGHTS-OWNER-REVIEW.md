# rekordbox MyTagSync — recipient-only candidate rights decision record

**Status: NOT APPROVED. No build artifact may be delivered using this document alone.**

This is a bounded evidence-to-decision record for a **single Windows 11 x64 read-only hardware acceptance** of the actual `rekordbox MyTagSync` candidate. It is not a license, an end-user agreement, legal advice, a waiver of Microsoft rights, or permission to enable Apply/Restore.

## Immutable software facts (re-confirm at decision time)

- The final intended source commit must have SHA-identical successful **FAST and full Windows FULL** jobs, including the real 388-member bundle audit, the older-Windows smoke, license/NOTICE capture, and `0` missing source identifiers. CI evidence is **technical**, not release authority.
- Actual publish: Windows `win-x64`, .NET SDK `10.0.402`, runtime and WindowsDesktop `10.0.13`, self-contained single-file WPF candidate with on-disk ZIP SHA-256 and sealed per-member checksums. Recheck versions and counts; do not accept historical hashes for newer builds.
- Current recorded full-text candidates, independently byte-frozen: `MICROSOFT_DOTNET_LIBRARY_LICENSE` from `dotnet/SqlClient:NOTICE.txt` (Git blob `0b936c6bb60efa99c0c1e4c853cc516eb3f7835b`) and `WINDOWS_SDK_D3DCOMPILER_LICENSE` from `microsoft/win32metadata:licenses/sdk_license.txt` (Git blob `efaf451b29e15921362cdb5888da9ee8f67acd1b`). **These are not evidence that the runtime or SDK instance was licensed under precisely those versions.** The SDK source specifies `WIN10SDK.RTM.AUG_2018_en-US`.
- The sealed compiler is `D3DCompiler_47_cor3.dll`: Microsoft WPF `v10.0.13` describes its rename from `d3dcompiler_47.dll`; the official SDK online REDIST listing includes the corresponding x64 compiler for *classic Windows applications*, subject to SDK terms.
- Original files are frozen inside the unreviewed evidence inventory. `missingNoticeSourceIds=[]` means **captured texts**, never satisfied contract terms.

## Decisions a competent distributor/rights reviewer must make

| Gate | Required *specific* demonstration | Until verified |
| --- | --- | --- |
| .NET Library agreement applicability | Establish from authoritative, appropriately versioned Microsoft product/pack licensing that the *exact* WindowsDesktop/.NET native runtime and single-file host in this candidate fall under the captured/current .NET Library agreement. Do not equate SqlClient's NOTICE with this product's own EULA. | `HUMAN_REVIEW_PENDING` |
| .NET Library redistribution duties | Confirm application-bound, non-standalone use; identify distributor, downstream/end-user protective terms, Microsoft trademark restrictions, third-party notice obligations, applicable indemnity/export/data provisions; determine whether recipient must affirm any terms before delivery. | `HUMAN_REVIEW_PENDING` |
| Windows SDK D3DCompiler conditions | Match *the exact sealed compiler/source-pack edition* to the applicable SDK license and REDIST.TXT inclusion, x64 platform and classic desktop application status. Determine whether the August 2018 license is actually the applicable version. Do not rely solely on renamed filename or an online list. | `HUMAN_REVIEW_PENDING` |
| Third-party and application rights | Check original Apache-2.0/MIT copyright, SQLite3MC bundle and Batteries_V2 dual notice, native/WPF additional notices, app ownership and generated assets; preserve original notices unmodified with recipient candidate as required. | `HUMAN_REVIEW_PENDING` |
| Private handoff | Independently **test**, without uploading the actual executable, a genuinely recipient-only, revocable, expiring, zero-cost channel. Public-repository Actions artifacts and ordinary public release assets are not acceptable. A draft release is only a hypothesis, not a proven control. Verify unauthorized access denied and accidental publication impossible or blocked. | `NO_CHANNEL_APPROVED` |
| Owner's informed decision | Provide actual applicable terms, obligations, exact source and candidate ZIP SHA-256, and recipient access method for a **separate explicit owner approval**. No scripts, CI or assistant should sign terms, opt in, authorize private upload or distribute a binary on the owner's behalf. | `NOT_REQUESTED` |
| Windows 11 hardware read-only acceptance | Only after delivery permission: one nonadmin Windows 11 x64 client check with independent ZIP hashes, persisted UI, source/target discovery, SQLCipher read-only validation, file identity, cue exclusions, mapped multivalue preview, foobar Bridge and XDJ-RX3 unchanged; never write music, tags, embedded cues, `master.db` or backup. | `NOT_RUN` |

## Evidence and decision fields (do not fill with inferred PASS)

- **Proposed immutable commit SHA:** NOT_SELECTED
- **Corresponding FAST/FULL run URLs and conclusions:** NOT_SELECTED
- **Proposed inner ZIP SHA-256:** NOT_SELECTED
- **Applicable .NET product license edition and exact upstream evidence:** NOT_PROVEN
- **Applicable compiler SDK edition and exact REDIST evidence:** NOT_PROVEN
- **Recipient-facing license/copyright/NOTICE obligations:** HUMAN_REVIEW_PENDING
- **Independent access-control negative test of handoff channel:** NOT_PERFORMED
- **Rights/terms reviewer and recorded authority:** NOT_IDENTIFIED
- **Separate informed recipient/owner approval for candidate delivery:** NOT_GIVEN
- **Windows 11 x64 read-only hardware acceptance:** PENDING
- **Live Apply/Restore or any real writes:** NOT_AUTHORIZED

## Fail-closed stop conditions

Any uncertain product license version; missing source-to-license relationship; contradictory manifest, tag or package digest; absent copyright/end-user terms; missing private-access negative test; unclear ownership or owner consent; candidate created after its qualified SHA; unsigned terms or changed upstream terms **blocks handoff**. Never flip `distributionLicenseStatus=REVIEW_REQUIRED_NOT_AUTHORIZED` or `completeness=NOT_ESTABLISHED` because original text capture, FAST, FULL, or the human *review checklist* is green.

Primary linked records: [Distribution gate](DISTRIBUTION-GATE.md), [Windows 11 acceptance](WINDOWS11-READONLY-ACCEPTANCE.md), [Issue #7](https://github.com/lxsdd/rekordbox-mytag-sync/issues/7). Official sources: https://dotnet.microsoft.com/en-us/dotnet_library_license.htm ; https://learn.microsoft.com/en-us/legal/windows-sdk/license ; https://learn.microsoft.com/en-us/legal/windows-sdk/redist ; https://github.com/dotnet/wpf/blob/v10.0.13/Documentation/redistributables.md .
