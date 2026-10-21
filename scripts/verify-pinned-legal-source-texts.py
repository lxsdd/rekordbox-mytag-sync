#!/usr/bin/env python3
"""Verify exact original, version-tagged legal source TEXTS (not release rights).

SHA-1 here is Git's blob object identity over [b'blob LENGTH\0' + bytes], NOT
a general security signature. SHA-256 is additionally recorded for the exact
bytes staged into a deterministic candidate. Never claim this discharges all
copyright/SDK/.NET Library obligations or approves delivery.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys
import tempfile

SOURCE = {
    "SQLITEPCLRAW_CORE_3_0_2_LICENSE": (
        "licenses/source-texts/SQLitePCLRaw-v3.0.2-LICENSE.txt",
        "d645695673349e3947e8e5ae42332d0ac3164cd7",
        "ericsink/SQLitePCL.raw@v3.0.2:LICENSE.TXT"),
    "SQLITEPCLRAW_CORE_3_0_2_NOTICE": (
        "licenses/source-texts/SQLitePCLRaw-v3.0.2-NOTICE.txt",
        "d728ea62485dc1f4a73fe54bbd73d23aa680d283",
        "ericsink/SQLitePCL.raw@v3.0.2:NOTICE.TXT"),
    "SQLITE3MC_LIB_2_4_0_LICENSE": (
        "licenses/source-texts/SQLite3MC-v2.4.0-LICENSE.txt",
        "2fff29de4dd1ec12dfdbfa36651cc9c42639579c",
        "utelle/SQLite3MultipleCiphers@v2.4.0:LICENSE"),
    "SQLITE3MC_NUGET_2_4_0_LICENSE": (
        "licenses/source-texts/SQLite3MC-NuGet-v2.4.0-LICENSE.txt",
        "94d3342e89d02360aad42e81ec6a599ecb9cb657",
        "utelle/SQLite3MultipleCiphers-NuGet@v2.4.0:LICENSE"),
    "DOTNET_CORE_MIT_LICENSE": (
        "licenses/source-texts/dotnet-core-MIT-LICENSE.txt",
        "984713a49622a96da110443c15477613bc12656b",
        "dotnet/core:LICENSE.TXT (source blob frozen; branch tag not fixed)"),
    "MICROSOFT_SQLITE_CORE_10_0_13_LICENSE": (
        "licenses/source-texts/Microsoft-Data-Sqlite-Core-v10.0.13-MIT-LICENSE.txt",
        "a616ed188dfce68ee308f674350ad242d8588c2b",
        "dotnet/efcore@v10.0.13:LICENSE.txt"),
    # Official Microsoft-owned originals containing the entire agreement,
    # preserved unmodified. The SqlClient NOTICE embeds .NET Library terms;
    # the win32metadata SDK agreement is explicitly EULAID WIN10SDK.RTM.AUG_2018.
    # Capturing source bytes does NOT resolve .NET 10.0.13 applicability,
    # redistribution, downstream terms or owner's legal acceptance.
    "MICROSOFT_DOTNET_LIBRARY_LICENSE": (
        "licenses/source-texts/dotnet-SqlClient-NOTICE-Library-License.txt",
        "0b936c6bb60efa99c0c1e4c853cc516eb3f7835b",
        "dotnet/SqlClient:NOTICE.txt (complete original source blob, untagged)"),
    "WINDOWS_SDK_D3DCOMPILER_LICENSE": (
        "licenses/source-texts/win32metadata-Windows10SDK-license.txt",
        "efaf451b29e15921362cdb5888da9ee8f67acd1b",
        "microsoft/win32metadata:licenses/sdk_license.txt (WIN10SDK.RTM.AUG_2018_en-US)"),
}
BLOCKED = "REVIEW_REQUIRED_NOT_AUTHORIZED"


class LegalEvidenceError(ValueError):
    pass


def git_blob_sha1(data):
    return hashlib.sha1(b"blob " + str(len(data)).encode("ascii") + b"\0" + data).hexdigest()


def verify(root, stage):
    root = Path(root).resolve()
    stage = Path(stage)
    if stage.exists():
        raise LegalEvidenceError("do not overwrite staged legal source evidence")
    names = set()
    prepared = []
    for sid, (rel, expected_blob, upstream) in sorted(SOURCE.items()):
        if sid in names:
            raise LegalEvidenceError("duplicate legal source ID")
        names.add(sid)
        path = root / rel
        if path.is_symlink() or not path.is_file():
            raise LegalEvidenceError("pinned legal source missing/symlinked: " + sid)
        payload = path.read_bytes()
        if not 20 <= len(payload) <= 1_000_000 or b"\x00" in payload:
            raise LegalEvidenceError("unsafe legal source text: " + sid)
        try:
            payload.decode("utf-8-sig", "strict")
        except UnicodeDecodeError as exc:
            raise LegalEvidenceError("invalid source encoding: " + sid) from exc
        if git_blob_sha1(payload) != expected_blob:
            raise LegalEvidenceError("actual license/NOTICE differs from upstream tagged blob: " + sid)
        prepared.append((sid, payload, upstream, expected_blob))

    # Exact Git-byte identity is not enough to assert that the intended
    # agreement occurs in an official NOTICE or that the SDK edition matches
    # the particular WPF D3DCompiler binary. Keep those distinctions explicit.
    library_notice = (root / SOURCE["MICROSOFT_DOTNET_LIBRARY_LICENSE"][0]).read_text(encoding="utf-8")
    sdk_agreement = (root / SOURCE["WINDOWS_SDK_D3DCOMPILER_LICENSE"][0]).read_text(encoding="utf-8")
    for text, tokens in (
        (library_notice, ("MICROSOFT .NET LIBRARY",
                          "DISTRIBUTABLE CODE", "Germany and Austria")),
        (sdk_agreement, ("MICROSOFT WINDOWS SOFTWARE DEVELOPMENT KIT",
                         "REDIST.TXT", "EULAID:WIN10SDK.RTM.AUG_2018_en-US"))):
        if any(token.casefold() not in text.casefold() for token in tokens):
            raise LegalEvidenceError("complete original Microsoft agreement scope markers absent")

    stage.mkdir(parents=True, exist_ok=False)
    items = []
    for sid, payload, upstream, blob in prepared:
        staged = sid + ".txt"
        (stage / staged).write_bytes(payload)
        items.append(dict(sourceId=sid, stagedName=staged,
                          upstreamGitSource=upstream, upstreamGitBlobSha1=blob,
                          bytes=len(payload),
                          sha256=hashlib.sha256(payload).hexdigest(),
                          verification="ORIGINAL_SOURCE_BYTES_ONLY",
                          noticeApplicability="REQUIRES_COMPONENT_REVIEW"))
    return dict(schemaVersion=1,
                microsoftAgreementSourcesCaptured=True,
                microsoftAgreementApplicability="HUMAN_VERSION_AND_DISTRIBUTION_REVIEW_PENDING",
                frozenSourceTexts=items,
                completeness="NOT_ESTABLISHED",
                distributionLicenseStatus=BLOCKED,
                sourceWarning="Source text identity is not licensing compliance, SDK redistributable approval, or delivery permission")


def selftest():
    with tempfile.TemporaryDirectory(prefix="mytagsync-legal-source-") as root_text:
        root = Path(root_text)
        # Synthetic data must never pass the real frozen upstream Git blob gate.
        for _, (rel, _, _) in SOURCE.items():
            p = root / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(b"SYNTHETIC TEST NOT AN UPSTREAM LEGAL TEXT\n")
        try:
            verify(root, root / "staged")
        except LegalEvidenceError as exc:
            assert "upstream tagged blob" in str(exc)
        else:
            raise AssertionError("synthetic/forged license content was accepted")
        data = b"SYNTHETIC GIT OBJECT WITHOUT ANY CLAIMED LICENSE\n"
        assert git_blob_sha1(data) == hashlib.sha1(
            b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
        assert git_blob_sha1(data) != git_blob_sha1(data + b" ")
        # Refuse a follow-on directory that might silently overwrite evidence.
        (root / "existing").mkdir()
        try:
            verify(root, root / "existing")
        except LegalEvidenceError:
            pass
        else:
            raise AssertionError("existing output was accepted")
    print("MYTAGSYNC_PINNED_LEGAL_SOURCE_TEXT_SELFTEST=PASS")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--source-root", type=Path)
    parser.add_argument("--stage-dir", type=Path)
    parser.add_argument("--output", type=Path)
    a = parser.parse_args()
    if a.self_test:
        selftest()
        return
    if not all((a.source_root, a.stage_dir, a.output)):
        parser.error("source-root, stage-dir and output are required")
    report = verify(a.source_root, a.stage_dir)
    a.output.parent.mkdir(parents=True, exist_ok=True)
    a.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"MYTAGSYNC_PINNED_LEGAL_SOURCE_TEXTS=PASS count={len(report['frozenSourceTexts'])} "
          "completeness=NOT_ESTABLISHED distribution=REVIEW_REQUIRED_NOT_AUTHORIZED")


if __name__ == "__main__":
    try:
        main()
    except (LegalEvidenceError, AssertionError, OSError) as exc:
        sys.exit("MYTAGSYNC_PINNED_LEGAL_SOURCE_TEXTS=FAIL " + str(exc))
