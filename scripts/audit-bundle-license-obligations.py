#!/usr/bin/env python3
"""Source-byte grounded .NET 10/SQLite license *obligation* matrix.

Records ALL embedded/sidecar components and the origin-dependent license
review groups. It cannot grant rights or establish complete copyright
notices. Never treat license hints or package metadata as distribution approval.
"""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import sys
import tempfile

LIBRARY_DLLS = {
    "coreclr.dll", "clrjit.dll", "hostpolicy.dll", "hostfxr.dll",
    "presentationnative_cor3.dll", "vcruntime140_cor3.dll",
    "wpfgfx_cor3.dll", "microsoft.diasymreader.native.x86.dll",
    "microsoft.diasymreader.native.amd64.dll",
    "microsoft.diasymreader.native.arm.dll",
    "microsoft.diasymreader.native.arm64.dll",
}
APP_OUTPUTS = {
    "rekordbox MyTagSync.dll",
    "rekordbox MyTagSync.deps.json",
    "rekordbox MyTagSync.runtimeconfig.json",
}
# Source IDs are explicitly version-pinned in the FULL workflow. A new
# package must be separately reviewed; do not default unfamiliar IDs to MIT.
MICROSOFT_RUNTIME = {
    "NETCORE_RUNTIME_10.0.13", "WINDOWSDESKTOP_RUNTIME_10.0.13",
    "NETCORE_PACK_10.0.13", "WINDOWSDESKTOP_PACK_10.0.13",
}
NUGET_MIT = {"MICROSOFT_SQLITE_CORE_10.0.13"}
SQLITE3MC = {
    "SQLITE3MC_BUNDLE_2.4.0", "SQLITE3MC_LIB_2.4.0",
    "SQLITE3MC_PROVIDER_2.4.0",
}
SQLITEPCL = {"SQLITEPCLRAW_CORE_3.0.2"}
KNOWN = MICROSOFT_RUNTIME | NUGET_MIT | SQLITE3MC | SQLITEPCL
NOTICE_GROUPS = {
    "DOTNET_LIBRARY_CONDITIONS",
    "DOTNET_MIT_COPYRIGHT_NOTICE",
    "WINDOWS_SDK_D3DCOMPILER_REDIST_RIGHTS",
    "SQLITE3MC_MIT_COPYRIGHT_NOTICE",
    "SQLITEPCLRAW_APACHE2_LICENSE_AND_NOTICE",
    "MICROSOFT_SQLITE_MIT_NOTICE",
    "APPLICATION_COPYRIGHT_REVIEW",
}
# Library terms also apply to the single-file native host, which can be
# prelinked and not appear as a separate embedded FileEntry.
CONTAINER_WIDE = ["DOTNET_LIBRARY_CONDITIONS"]


class MatrixError(ValueError):
    pass


def assert_sha(value, label):
    if (not isinstance(value, str) or len(value) != 64 or
            any(ch not in "0123456789abcdef" for ch in value)):
        raise MatrixError("invalid SHA-256 " + label)


def source_group(item, source_id):
    name = item["name"].rsplit("/", 1)[-1].casefold()
    if source_id in MICROSOFT_RUNTIME:
        if name == "d3dcompiler_47_cor3.dll":
            return "WINDOWS_SDK_D3DCOMPILER_REDIST_RIGHTS"
        if name in LIBRARY_DLLS:
            return "DOTNET_LIBRARY_CONDITIONS"
        return "DOTNET_MIT_COPYRIGHT_NOTICE"
    if source_id in NUGET_MIT:
        return "MICROSOFT_SQLITE_MIT_NOTICE"
    if source_id in SQLITE3MC:
        return "SQLITE3MC_MIT_COPYRIGHT_NOTICE"
    if source_id in SQLITEPCL:
        return "SQLITEPCLRAW_APACHE2_LICENSE_AND_NOTICE"
    raise MatrixError("unrecognized source-package identity")


def build(bundle, notice_manifest, notice_root):
    if bundle.get("distributionLicenseStatus") != "REVIEW_REQUIRED_NOT_AUTHORIZED":
        raise MatrixError("bundle license gate unexpectedly lifted")
    src = bundle.get("sourceByteEvidence", {})
    app = bundle.get("applicationOutputEvidence", {})
    if (src.get("status") != "REVIEW_REQUIRED_NOT_AUTHORIZED" or
            app.get("status") != "APPLICATION_OUTPUT_IDENTITY_REVIEW_REQUIRED" or
            app.get("distributionLicenseStatus") != "REVIEW_REQUIRED_NOT_AUTHORIZED" or
            app.get("unexpectedUnmatchedCount") != 0):
        raise MatrixError("missing or unauthorized source/application provenance")
    entries = bundle["embeddedFiles"] + bundle["externalFiles"]
    if len(entries) != bundle["embeddedFileCount"] + bundle["externalFileCount"]:
        raise MatrixError("inconsistent sealed bundle inventory")
    indexed = {}
    for item in entries:
        name = item["name"]
        if name in indexed or name.startswith(("/", "\\")) or ".." in name.split("/"):
            raise MatrixError("duplicate/unsafe sealed member identity")
        digest = item.get("onDiskSha256", item.get("sha256"))
        assert_sha(digest, name)
        indexed[name] = item
    evidence = src.get("components", [])
    if (len(evidence) != len(indexed) or
            len({r["name"] for r in evidence}) != len(evidence) or
            {r["name"] for r in evidence} != set(indexed)):
        raise MatrixError("source-byte proof does not cover every sealed member")
    app_rows = app.get("applicationOutputs", [])
    if len(app_rows) != 3 or {r["name"] for r in app_rows} != APP_OUTPUTS:
        raise MatrixError("application-output boundary changed")

    components = []
    used = set(CONTAINER_WIDE)
    groups_by_name = {}
    for row in evidence:
        item = indexed[row["name"]]
        name = row["name"]
        if item["licenseReview"] != row["licenseReview"]:
            raise MatrixError("review category changed for " + name)
        decision = row["status"]
        candidates = row.get("possibleSources", [])
        if name in APP_OUTPUTS:
            expected_app = next(a for a in app_rows if a["name"] == name)
            if expected_app["sha256"] != item.get("onDiskSha256", item.get("sha256")):
                raise MatrixError("app output bytes do not match attestation")
            if decision != expected_app["sourceStatus"] or decision not in (
                    "UNMATCHED_REQUIRES_REVIEW", "MATCHED_EXACT_RAW_BYTES"):
                raise MatrixError("app output origin changed")
            groups = {"APPLICATION_COPYRIGHT_REVIEW"}
        else:
            if decision != "MATCHED_EXACT_RAW_BYTES" or not candidates:
                raise MatrixError("foreign member lacks exact package byte match: " + name)
            groups = set()
            for candidate in candidates:
                ident = candidate["sourceId"]
                relative = candidate["relativePath"]
                if ident not in KNOWN or relative.startswith(("/", "\\")) or (
                        any(part in ("", ".", "..") for part in relative.split("/"))):
                    raise MatrixError("unknown source label or unsafe relative path")
                groups.add(source_group(item, ident))
            # A package can embed a transitive dependency with distinct terms.
            # Every plausible set of applicable notices is retained.
            if name.rsplit("/", 1)[-1].casefold().startswith("sqlitepclraw.") and (
                    "SQLITE3MC_MIT_COPYRIGHT_NOTICE" in groups):
                groups.add("SQLITEPCLRAW_APACHE2_LICENSE_AND_NOTICE")
            if item["licenseReview"] == "MICROSOFT_WINDOWS_SDK_LICENSE_REVIEW" and (
                    "WINDOWS_SDK_D3DCOMPILER_REDIST_RIGHTS" not in groups):
                raise MatrixError("D3DCompiler licensing exception misattributed")
            if item["licenseReview"] == "MICROSOFT_DOTNET_LIBRARY_LICENSE_REVIEW" and (
                    "DOTNET_LIBRARY_CONDITIONS" not in groups):
                raise MatrixError("native .NET Library licensing exception misattributed")
        if not groups <= NOTICE_GROUPS:
            raise MatrixError("unknown obligation group")
        used.update(groups)
        groups_by_name[name] = sorted(groups)
        components.append(dict(name=name,
                               sealedSha256=item.get("onDiskSha256", item.get("sha256")),
                               sourceByteStatus=decision,
                               obligationGroups=sorted(groups),
                               sourceCandidates=len(candidates)))
    if (src["counts"]["MATCHED_EXACT_RAW_BYTES"] +
            src["counts"]["UNMATCHED_REQUIRES_REVIEW"] +
            src["counts"]["COMPRESSED_UNRESOLVED"] != len(components)):
        raise MatrixError("source totals are inconsistent")

    if (notice_manifest.get("distributionLicenseStatus") != "REVIEW_REQUIRED_NOT_AUTHORIZED" or
            notice_manifest.get("completeness") != "NOT_ESTABLISHED"):
        raise MatrixError("notice source clearance was claimed without review")
    text_rows = notice_manifest.get("sourceTexts", [])
    if not text_rows or len({x["sourceId"] for x in text_rows}) != len(text_rows):
        raise MatrixError("missing/duplicate notice-source evidence")
    if not {"DOTNET_INSTALLATION_LICENSE", "DOTNET_INSTALLATION_THIRDPARTY"} <= {
            x["sourceId"] for x in text_rows}:
        raise MatrixError("required installed .NET notice evidence missing")
    verified_texts = []
    for n in sorted(text_rows, key=lambda x: x["sourceId"]):
        name = n["stagedName"]
        if name != n["sourceId"] + ".txt" or "/" in name or "\\" in name:
            raise MatrixError("unsafe notice evidence filename")
        data = (Path(notice_root) / name).read_bytes()
        if len(data) != n["bytes"] or hashlib.sha256(data).hexdigest() != n["sha256"]:
            raise MatrixError("notice evidence bytes changed: " + n["sourceId"])
        verified_texts.append(dict(sourceId=n["sourceId"], sha256=n["sha256"],
                                   bytes=n["bytes"], status="SOURCE_TEXT_ONLY"))
    # Pinned installed .NET texts are not a proof that WPF/SDK/Apache/MIT
    # source-specific conditions or all copyright holders are satisfied.
    return dict(schemaVersion=1,
                provenanceScope="EXACT_SOURCE_BYTES_PLUS_CONSERVATIVE_LICENSE_OBLIGATIONS",
                componentCount=len(components),
                groups=sorted(used),
                countsByGroup=dict(sorted(Counter(g for c in components
                    for g in c["obligationGroups"]).items())),
                containerWideObligations=CONTAINER_WIDE,
                noticeSourceEvidence=verified_texts,
                outstandingReviewGroups=sorted(used),
                noticeCompleteness="NOT_ESTABLISHED",
                distributionLicenseStatus="REVIEW_REQUIRED_NOT_AUTHORIZED",
                components=sorted(components, key=lambda x: x["name"].casefold()))


def selftest():
    with tempfile.TemporaryDirectory(prefix="mytagsync-matrix-") as tmp:
        root = Path(tmp)
        (root / "DOTNET_INSTALLATION_LICENSE.txt").write_bytes(b"FICTIONAL DOTNET LICENSE TEXT\n")
        (root / "DOTNET_INSTALLATION_THIRDPARTY.txt").write_bytes(b"FICTIONAL NOTICES FOR TEST\n")
        texts = [
            dict(sourceId=n, stagedName=n + ".txt",
                 sha256=hashlib.sha256((root / (n + ".txt")).read_bytes()).hexdigest(),
                 bytes=(root / (n + ".txt")).stat().st_size)
            for n in ("DOTNET_INSTALLATION_LICENSE", "DOTNET_INSTALLATION_THIRDPARTY")]
        notices = dict(distributionLicenseStatus="REVIEW_REQUIRED_NOT_AUTHORIZED",
                       completeness="NOT_ESTABLISHED", sourceTexts=texts)
        names = [
            ("D3DCompiler_47_cor3.dll", "MICROSOFT_WINDOWS_SDK_LICENSE_REVIEW",
             "WINDOWSDESKTOP_RUNTIME_10.0.13"),
            ("System.Text.Json.dll", "MICROSOFT_COMPONENT_LICENSE_REVIEW",
             "NETCORE_RUNTIME_10.0.13"),
            ("e_sqlite3mc.dll", "THIRD_PARTY_SQLITE3MC_LICENSE_REVIEW",
             "SQLITE3MC_LIB_2.4.0"),
            ("SQLitePCLRaw.core.dll", "LOCKED_NUGET_SQLITEPCLRAW_ATTRIBUTION_PENDING",
             "SQLITEPCLRAW_CORE_3.0.2"),
        ]
        names += [(n, "APP_METADATA" if n.endswith(".json") else
                   "APPLICATION_OWNERSHIP_REVIEW", None) for n in APP_OUTPUTS]
        components = []
        origins = []
        for name, category, origin in names:
            components.append(dict(name=name, licenseReview=category, onDiskSha256="a"*64))
            origins.append(dict(name=name, licenseReview=category,
                                status=("MATCHED_EXACT_RAW_BYTES" if origin else
                                        "UNMATCHED_REQUIRES_REVIEW"),
                                possibleSources=[dict(sourceId=origin, relativePath=name)]
                                if origin else []))
        bundle = dict(distributionLicenseStatus="REVIEW_REQUIRED_NOT_AUTHORIZED",
                      embeddedFileCount=len(components), externalFileCount=0,
                      embeddedFiles=components, externalFiles=[],
                      sourceByteEvidence=dict(status="REVIEW_REQUIRED_NOT_AUTHORIZED",
                          counts=dict(MATCHED_EXACT_RAW_BYTES=4,
                                      UNMATCHED_REQUIRES_REVIEW=3,
                                      COMPRESSED_UNRESOLVED=0), components=origins),
                      applicationOutputEvidence=dict(
                          status="APPLICATION_OUTPUT_IDENTITY_REVIEW_REQUIRED",
                          distributionLicenseStatus="REVIEW_REQUIRED_NOT_AUTHORIZED",
                          unexpectedUnmatchedCount=0,
                          applicationOutputs=[dict(name=n, sha256="a"*64,
                              sourceStatus="UNMATCHED_REQUIRES_REVIEW") for n in APP_OUTPUTS]))
        valid = build(bundle, notices, root)
        assert valid["componentCount"] == 7
        assert valid["noticeCompleteness"] == "NOT_ESTABLISHED"
        assert valid["distributionLicenseStatus"] == "REVIEW_REQUIRED_NOT_AUTHORIZED"
        assert "WINDOWS_SDK_D3DCOMPILER_REDIST_RIGHTS" in valid["groups"]
        assert "SQLITEPCLRAW_APACHE2_LICENSE_AND_NOTICE" in valid["groups"]
        def must_reject(sample, n=notices):
            try:
                build(sample, n, root)
            except (MatrixError, OSError, KeyError, ValueError):
                return
            raise AssertionError("invalid obligation/notice input accepted")
        altered = json.loads(json.dumps(bundle))
        altered["sourceByteEvidence"]["components"][0]["possibleSources"][0]["sourceId"] = (
            "OTHER_LICENSE")
        must_reject(altered)
        altered = json.loads(json.dumps(bundle))
        altered["sourceByteEvidence"]["components"][2]["status"] = "UNMATCHED_REQUIRES_REVIEW"
        must_reject(altered)
        altered = json.loads(json.dumps(bundle))
        altered["embeddedFiles"][0]["licenseReview"] = "THIRD_PARTY_SQLITE3MC_LICENSE_REVIEW"
        must_reject(altered)
        altered = json.loads(json.dumps(bundle))
        altered["applicationOutputEvidence"]["applicationOutputs"][0]["sha256"] = "b"*64
        must_reject(altered)
        altered = json.loads(json.dumps(bundle))
        altered["sourceByteEvidence"]["components"].pop()
        must_reject(altered)
        (root / "DOTNET_INSTALLATION_LICENSE.txt").write_bytes(b"CHANGED")
        must_reject(bundle)
    print("MYTAGSYNC_COMPONENT_LICENSE_MATRIX_SELFTEST=PASS")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--self-test", action="store_true")
    p.add_argument("--bundle", type=Path)
    p.add_argument("--notice-manifest", type=Path)
    p.add_argument("--notice-dir", type=Path)
    p.add_argument("--output", type=Path)
    args = p.parse_args()
    if args.self_test:
        selftest()
        return
    if not all((args.bundle, args.notice_manifest, args.notice_dir, args.output)):
        p.error("all bundle/notice/output inputs are mandatory")
    bundle = json.loads(args.bundle.read_text(encoding="utf-8-sig"))
    notices = json.loads(args.notice_manifest.read_text(encoding="utf-8-sig"))
    report = build(bundle, notices, args.notice_dir)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"MYTAGSYNC_COMPONENT_LICENSE_MATRIX=PASS "
          f"components={report['componentCount']} "
          f"obligation_groups={len(report['groups'])} "
          "notice_completeness=NOT_ESTABLISHED "
          "distribution=REVIEW_REQUIRED_NOT_AUTHORIZED")


if __name__ == "__main__":
    try:
        main()
    except (MatrixError, AssertionError, OSError, ValueError, KeyError) as exc:
        sys.exit("MYTAGSYNC_COMPONENT_LICENSE_MATRIX=FAIL " + str(exc))
