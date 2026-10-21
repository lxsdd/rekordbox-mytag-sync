#!/usr/bin/env python3
"""Fail-closed, file-by-file NOTICE source coverage for a sealed Windows bundle.

This is an evidence-gap report, NOT a legal opinion, copyright attestation,
release authorization, or substitute for actual applicable ThirdPartyNotices.
Inputs contain synthetic/CI bundle relative names and source-file hashes only.
Never read a user music folder, master.db, private path, or audio content.
"""
import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path
import re
import sys
import tempfile

BLOCKED = "REVIEW_REQUIRED_NOT_AUTHORIZED"
HEX256 = re.compile(r"[0-9a-f]{64}\Z")
SAFE_NAME = re.compile(r"[A-Za-z0-9_.+/-]{1,240}\Z")
APP_NAMES = frozenset({
    "rekordbox MyTagSync.dll",
    "rekordbox MyTagSync.deps.json",
    "rekordbox MyTagSync.runtimeconfig.json",
})

# These are REQUIRED SOURCE IDENTIFIERS for a later qualified rights review,
# not claims that any one captured text already discharges an obligation.
REQUIREMENTS = {
    "MICROSOFT_RUNTIME": ("DOTNET_INSTALLATION_LICENSE",
                          "DOTNET_INSTALLATION_THIRDPARTY"),
    "MICROSOFT_DOTNET_LIBRARY": ("MICROSOFT_DOTNET_LIBRARY_LICENSE",),
    "WINDOWS_SDK_D3DCOMPILER": ("WINDOWS_SDK_D3DCOMPILER_LICENSE",),
    "SQLITE3MC_MIT": ("SQLITE3MC_LIB_2_4_0_LICENSE",),
    "SQLITEPCLRAW_APACHE_2": ("SQLITEPCLRAW_CORE_3_0_2_LICENSE",),
    # Conservative union: bundled Batteries implementation + packaging wrapper
    # each needs human copyright/license applicability review.
    "SQLITEPCLRAW_BATTERIES_BUNDLE": (
        "SQLITEPCLRAW_CORE_3_0_2_LICENSE",
        "SQLITE3MC_NUGET_2_4_0_LICENSE"),
    "MICROSOFT_SQLITE_NUGET_MIT": ("MICROSOFT_SQLITE_CORE_10_0_13_LICENSE",),
}
MICROSOFT_RUNTIME_CATEGORIES = {
    "MICROSOFT_WPF_PACK_ATTRIBUTION_PENDING",
    "MICROSOFT_WPF_SATELLITE_ATTRIBUTION_PENDING",
    "MICROSOFT_NET_RUNTIME_ATTRIBUTION_PENDING",
    "MICROSOFT_COMPONENT_LICENSE_REVIEW",
}
APP_CATEGORIES = {"APPLICATION_OWNERSHIP_REVIEW", "APP_METADATA"}



# Locked inputs from FULL's exact .NET 10.0.13 / SQLite package-source roots.
# This links a claimed legal family to the *actual* byte-identical package
# origin, rather than trusting a mutable licenseReview string alone.
ORIGIN_GROUPS = {
    "NETCORE_RUNTIME_10.0.13": "DOTNET",
    "NETCORE_PACK_10.0.13": "DOTNET",
    "WINDOWSDESKTOP_RUNTIME_10.0.13": "WINDOWSDESKTOP",
    "WINDOWSDESKTOP_PACK_10.0.13": "WINDOWSDESKTOP",
    "SQLITE3MC_BUNDLE_2.4.0": "SQLITE3MC",
    "SQLITE3MC_LIB_2.4.0": "SQLITE3MC",
    "SQLITE3MC_PROVIDER_2.4.0": "SQLITE3MC",
    "SQLITEPCLRAW_CORE_3.0.2": "SQLITEPCLRAW",
    "MICROSOFT_SQLITE_CORE_10.0.13": "MICROSOFT_SQLITE",
}
FAMILY_ORIGIN_GROUPS = {
    "MICROSOFT_RUNTIME": {"DOTNET", "WINDOWSDESKTOP"},
    "MICROSOFT_DOTNET_LIBRARY": {"DOTNET", "WINDOWSDESKTOP"},
    "WINDOWS_SDK_D3DCOMPILER": {"WINDOWSDESKTOP"},
    "SQLITE3MC_MIT": {"SQLITE3MC"},
    "SQLITEPCLRAW_APACHE_2": {"SQLITEPCLRAW"},
    # Batteries_V2 is actually staged by SQLite3MC.PCLRaw.bundle 2.4.0,
    # not by the SQLitePCLRaw.core package. Do NOT generalize this alias.
    "SQLITEPCLRAW_BATTERIES_BUNDLE": {"SQLITE3MC"},
    "MICROSOFT_SQLITE_NUGET_MIT": {"MICROSOFT_SQLITE"},
}


def require_compatible_origins(family, origins, name):
    allowed = FAMILY_ORIGIN_GROUPS.get(family)
    require(allowed is not None and origins,
            "missing allowed origin family or provenance: " + name)
    for origin in origins:
        require(ORIGIN_GROUPS.get(origin) in allowed,
                "origin package contradicts license family: " + name)
        if family == "SQLITEPCLRAW_BATTERIES_BUNDLE":
            require(origin == "SQLITE3MC_BUNDLE_2.4.0",
                    "Batteries DLL unexpectedly sourced from another package")

class CoverageError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise CoverageError(message)


def clean_member(value):
    # Only the three verified, exactly spelled application output names may
    # contain spaces; do not relax the path/foreign-member character policy.
    require(isinstance(value, str) and
            (value in APP_NAMES or SAFE_NAME.fullmatch(value) is not None),
            "invalid embedded member name")
    require(not value.startswith("/") and
            all(p not in ("", ".", "..") for p in value.split("/")),
            "unsafe embedded member path")
    return value


def text_sources(data):
    require(data.get("distributionLicenseStatus") == BLOCKED and
            data.get("completeness") == "NOT_ESTABLISHED",
            "notice-source manifest incorrectly claims clearance")
    values = data.get("sourceTexts")
    require(isinstance(values, list) and len(values) > 0,
            "notice source evidence missing")
    result = {}
    for record in values:
        require(isinstance(record, dict), "invalid notice source")
        sid = record.get("sourceId")
        require(isinstance(sid, str) and re.fullmatch(r"[A-Z][A-Z0-9_]{3,95}", sid)
                and sid not in result, "unsafe/duplicate notice ID")
        require(record.get("stagedName") == sid + ".txt"
                and isinstance(record.get("bytes"), int)
                and record["bytes"] >= 20
                and isinstance(record.get("sha256"), str)
                and HEX256.fullmatch(record["sha256"]) is not None
                and record.get("status") == "RAW_SOURCE_TEXT_UNREVIEWED",
                "invalid or falsely approved captured license source")
        result[sid] = dict(sha256=record["sha256"], bytes=record["bytes"])
    return result


def family_for(member, candidate_sources):
    category = member["licenseReview"]
    if member["name"] in APP_NAMES:
        require(category in APP_CATEGORIES, "altered app output attribution")
        return "APPLICATION_RIGHTS"
    # Unlike names inferred from generic "Microsoft./System." categories,
    # these two known sealed DLLs have distinct, verified source owners.
    # Their rights family cannot be re-selected by spoofing a candidate ID.
    exact_library_families = {
        "system.private.corelib.dll": "MICROSOFT_RUNTIME",
        "microsoft.data.sqlite.dll": "MICROSOFT_SQLITE_NUGET_MIT",
    }
    canonical_name = member["name"].casefold()
    if canonical_name in exact_library_families:
        require(category == "MICROSOFT_COMPONENT_LICENSE_REVIEW",
                "known Microsoft assembly changed legal category")
        return exact_library_families[canonical_name]
    if member["name"].casefold() == "sqlitepclraw.batteries_v2.dll":
        require(category == "LOCKED_NUGET_SQLITEPCLRAW_ATTRIBUTION_PENDING",
                "bundled Batteries DLL changed expected attribution category")
        return "SQLITEPCLRAW_BATTERIES_BUNDLE"
    if category == "MICROSOFT_WINDOWS_SDK_LICENSE_REVIEW":
        return "WINDOWS_SDK_D3DCOMPILER"
    if category == "MICROSOFT_DOTNET_LIBRARY_LICENSE_REVIEW":
        return "MICROSOFT_DOTNET_LIBRARY"
    if category == "THIRD_PARTY_SQLITE3MC_LICENSE_REVIEW":
        return "SQLITE3MC_MIT"
    if category == "LOCKED_NUGET_SQLITEPCLRAW_ATTRIBUTION_PENDING":
        return "SQLITEPCLRAW_APACHE_2"
    candidates = set(candidate_sources)
    if any(x.startswith("SQLITE3MC_") for x in candidates):
        return "SQLITE3MC_MIT"
    if any(x.startswith("SQLITEPCLRAW_") for x in candidates):
        return "SQLITEPCLRAW_APACHE_2"
    if "MICROSOFT_SQLITE_CORE_10.0.13" in candidates:
        return "MICROSOFT_SQLITE_NUGET_MIT"
    if category in MICROSOFT_RUNTIME_CATEGORIES:
        return "MICROSOFT_RUNTIME"
    # An unrecognized category is not silently attached to a generic MIT text.
    raise CoverageError("unrecognized packaged license family: " +
                        clean_member(member["name"]))


def report_coverage(bundle, manifest):
    require(bundle.get("distributionLicenseStatus") == BLOCKED,
            "bundle incorrectly claims legal clearance")
    sources = text_sources(manifest)
    embedded = bundle.get("embeddedFiles")
    external = bundle.get("externalFiles")
    require(isinstance(embedded, list) and isinstance(external, list),
            "missing sealed member arrays")
    members = embedded + external
    require(len(members) == bundle.get("embeddedFileCount", -1) +
            bundle.get("externalFileCount", -1) and len(members) > 0,
            "bundle member count mismatch")
    source_evidence = bundle.get("sourceByteEvidence", {})
    require(source_evidence.get("status") == BLOCKED and
            isinstance(source_evidence.get("components"), list),
            "exact source-byte provenance not available")
    exact = {}
    for record in source_evidence["components"]:
        name = clean_member(record.get("name"))
        require(name not in exact and
                record.get("status") in
                {"MATCHED_EXACT_RAW_BYTES", "UNMATCHED_REQUIRES_REVIEW"},
                "ambiguous/compressed/unrecognized byte provenance")
        possible = record.get("possibleSources", [])
        require(isinstance(possible, list), "invalid source candidates")
        source_ids = []
        for item in possible:
            require(isinstance(item, dict) and
                    isinstance(item.get("sourceId"), str) and
                    re.fullmatch(r"[A-Z0-9][A-Z0-9._-]{2,100}",
                                 item["sourceId"]) is not None and
                    isinstance(item.get("relativePath"), str),
                    "unsafe source label")
            clean_member(item["relativePath"])
            source_ids.append(item["sourceId"])
        if record["status"] == "MATCHED_EXACT_RAW_BYTES":
            require(len(source_ids) > 0, "source match has no source identity")
        else:
            require(len(source_ids) == 0, "unmatched bytes reported as matched")
        exact[name] = (record["status"], sorted(set(source_ids)),
                       record.get("licenseReview"))

    observed = {}
    rows = []
    requirements = defaultdict(set)
    per_family = Counter()
    per_state = Counter()
    for item in members:
        name = clean_member(item.get("name"))
        require(name not in observed and
                isinstance(item.get("licenseReview"), str),
                "duplicate/missing member license category")
        observed[name] = item
        checksum = item.get("onDiskSha256", item.get("sha256"))
        require(isinstance(checksum, str) and HEX256.fullmatch(checksum) is not None,
                "missing actual sealed member checksum")
        require(name in exact, "bundle member lacks byte-origin evidence")
        status, origins, source_license = exact[name]
        require(source_license == item["licenseReview"],
                "sealed member contradicts source-byte license attribution")
        if name not in APP_NAMES:
            require(status == "MATCHED_EXACT_RAW_BYTES",
                    "unknown foreign packaged bytes cannot be covered by notices")
        else:
            require(status == "UNMATCHED_REQUIRES_REVIEW" or
                    status == "MATCHED_EXACT_RAW_BYTES",
                    "application output identity is not qualified")
        family = family_for(item, origins)
        if family != "APPLICATION_RIGHTS":
            require_compatible_origins(family, origins, name)
        needed = list(REQUIREMENTS.get(family, ()))
        if family == "APPLICATION_RIGHTS":
            require(name in APP_NAMES, "unlisted application right")
            state = "APPLICATION_COPYRIGHT_REVIEW_REQUIRED"
            absent = []
        else:
            require(needed, "license family missing notice obligations")
            absent = sorted(set(needed) - sources.keys())
            state = ("NOTICE_TEXT_MISSING_REVIEW_REQUIRED" if absent else
                     "NOTICE_SOURCE_CAPTURED_APPLICABILITY_REVIEW_REQUIRED")
            for source_id in needed:
                requirements[source_id].add(name)
        per_family[family] += 1
        per_state[state] += 1
        rows.append(dict(name=name, bundleSha256=checksum,
                         sourceByteStatus=status, pinnedSourceIds=origins,
                         family=family, requiredNoticeSourceIds=needed,
                         missingNoticeSourceIds=absent,
                         state=state))
    require(len(exact) == len(observed) and set(exact) == set(observed),
            "extra/missing byte-evidence rows")
    require(set(APP_NAMES).issubset(observed),
            "missing expected application-generated members")
    all_missing = sorted(set(requirements) - sources.keys())
    require(len(rows) == len(members), "notice matrix failed complete coverage")
    rows.sort(key=lambda row: row["name"].casefold())
    return dict(schemaVersion=1, bundleSha256=bundle.get("bundleSha256"),
                coveredMemberCount=len(rows),
                distributionLicenseStatus=BLOCKED,
                completeness="NOT_ESTABLISHED",
                copyrightAndLicenseApplicability="HUMAN_REVIEW_PENDING",
                missingNoticeSourceIds=all_missing,
                noticeSourceFilesCaptured=len(sources),
                sourcesPresent=sorted(sources),
                familyCounts=dict(sorted(per_family.items())),
                reviewStateCounts=dict(sorted(per_state.items())),
                requirements=[dict(sourceId=sid, captured=sid in sources,
                                   affectedMembers=len(names))
                              for sid, names in sorted(requirements.items())],
                members=rows)


def selftest():
    def member(name, label, hash_byte="a"):
        return dict(name=name, licenseReview=label,
                    onDiskSha256=hash_byte * 64)
    app = [(name, "APP_METADATA" if name.endswith(".json") else
            "APPLICATION_OWNERSHIP_REVIEW") for name in sorted(APP_NAMES)]
    samples = [
        ("System.Private.CoreLib.dll", "MICROSOFT_COMPONENT_LICENSE_REVIEW",
         "NETCORE_PACK_10.0.13"),
        ("D3DCompiler_47_cor3.dll", "MICROSOFT_WINDOWS_SDK_LICENSE_REVIEW",
         "WINDOWSDESKTOP_PACK_10.0.13"),
        ("e_sqlite3mc.dll", "THIRD_PARTY_SQLITE3MC_LICENSE_REVIEW",
         "SQLITE3MC_LIB_2.4.0"),
        ("SQLitePCLRaw.core.dll", "LOCKED_NUGET_SQLITEPCLRAW_ATTRIBUTION_PENDING",
         "SQLITEPCLRAW_CORE_3.0.2"),
        ("Microsoft.Data.Sqlite.dll", "MICROSOFT_COMPONENT_LICENSE_REVIEW",
         "MICROSOFT_SQLITE_CORE_10.0.13"),
        ("SQLitePCLRaw.batteries_v2.dll",
         "LOCKED_NUGET_SQLITEPCLRAW_ATTRIBUTION_PENDING",
         "SQLITE3MC_BUNDLE_2.4.0"),
    ]
    entries = [member(n, k) for n, k, _ in samples]
    entries += [member(name, category) for name, category in app]
    def origin(n, source, classification):
        return dict(name=n, licenseReview=classification,
                    status="MATCHED_EXACT_RAW_BYTES" if source else
                           "UNMATCHED_REQUIRES_REVIEW",
                    possibleSources=([dict(sourceId=source, relativePath=n)]
                                     if source else []))
    origin_rows = [origin(n, sid, category) for n, category, sid in samples]
    origin_rows += [origin(n, None, category) for n, category in app]
    bundle = dict(distributionLicenseStatus=BLOCKED, bundleSha256="b" * 64,
                  embeddedFileCount=len(entries), externalFileCount=0,
                  embeddedFiles=entries, externalFiles=[],
                  sourceByteEvidence=dict(status=BLOCKED, components=origin_rows))
    def captured(sid):
        return dict(sourceId=sid, stagedName=sid + ".txt", bytes=100,
                    sha256="a" * 64, status="RAW_SOURCE_TEXT_UNREVIEWED")
    source = dict(distributionLicenseStatus=BLOCKED,
                  completeness="NOT_ESTABLISHED",
                  sourceTexts=[captured("DOTNET_INSTALLATION_LICENSE"),
                               captured("DOTNET_INSTALLATION_THIRDPARTY")])
    report = report_coverage(bundle, source)
    assert report["coveredMemberCount"] == len(entries)
    assert set(report["missingNoticeSourceIds"]) == {
        "WINDOWS_SDK_D3DCOMPILER_LICENSE",
        "SQLITE3MC_LIB_2_4_0_LICENSE",
        "SQLITEPCLRAW_CORE_3_0_2_LICENSE",
        "MICROSOFT_SQLITE_CORE_10_0_13_LICENSE"}
    assert report["distributionLicenseStatus"] == BLOCKED
    assert report["completeness"] == "NOT_ESTABLISHED"
    for sid in report["missingNoticeSourceIds"]:
        source["sourceTexts"].append(captured(sid))
    assert not report_coverage(bundle, source)["missingNoticeSourceIds"]
    assert report_coverage(bundle, source)["completeness"] == "NOT_ESTABLISHED"
    # The same exact assembly may appear in a runtime and targeting pack;
    # multiple byte-identical candidates within the same rights group pass.
    aligned = json.loads(json.dumps(bundle))
    aligned["sourceByteEvidence"]["components"][0]["possibleSources"].append(
        dict(sourceId="NETCORE_RUNTIME_10.0.13",
             relativePath="System.Private.CoreLib.dll"))
    assert report_coverage(aligned, source)["distributionLicenseStatus"] == BLOCKED
    def bad(mutator):
        b = json.loads(json.dumps(bundle))
        s = json.loads(json.dumps(source))
        mutator(b, s)
        try:
            report_coverage(b, s)
        except CoverageError:
            return
        raise AssertionError("invalid coverage evidence accepted")
    bad(lambda b, s: b["embeddedFiles"].pop())
    bad(lambda b, s: b["sourceByteEvidence"]["components"].pop())
    bad(lambda b, s: b["embeddedFiles"][0].update(name="../private.mp3"))
    bad(lambda b, s: b["embeddedFiles"][0].update(licenseReview="SOME_VENDOR"))
    bad(lambda b, s: b["embeddedFiles"][0].update(onDiskSha256="not-a-hash"))
    # Raw byte identity alone must not allow a contradictory legal-family
    # source ledger, whether the entry is third-party or first-party output.
    bad(lambda b, s: b["sourceByteEvidence"]["components"][0].update(
        licenseReview="MICROSOFT_WINDOWS_SDK_LICENSE_REVIEW"))
    bad(lambda b, s: b["sourceByteEvidence"]["components"][-1].update(
        licenseReview="THIRD_PARTY_SQLITE3MC_LICENSE_REVIEW"))
    # A forged package ID can match a member's textual license category,
    # yet point to a different vendor/version and therefore wrong notices.
    bad(lambda b, s: b["sourceByteEvidence"]["components"][0][
        "possibleSources"][0].update(sourceId="SQLITEPCLRAW_CORE_3.0.2"))
    bad(lambda b, s: b["sourceByteEvidence"]["components"][1][
        "possibleSources"][0].update(sourceId="NETCORE_RUNTIME_10.0.13"))
    bad(lambda b, s: b["sourceByteEvidence"]["components"][2][
        "possibleSources"][0].update(sourceId="MICROSOFT_SQLITE_CORE_10.0.13"))
    bad(lambda b, s: b["sourceByteEvidence"]["components"][3][
        "possibleSources"][0].update(sourceId="SQLITE3MC_PROVIDER_2.4.0"))
    bad(lambda b, s: b["sourceByteEvidence"]["components"][4][
        "possibleSources"][0].update(sourceId="SQLITEPCLRAW_CORE_3.0.2"))
    bad(lambda b, s: b["sourceByteEvidence"]["components"][0][
        "possibleSources"][0].update(sourceId="UNKNOWN_PACK_1.2.3"))
    # Only the specific Batteries_V2 DLL may be accepted from the bundle,
    # and its wrapper+PCLRaw notice sources cannot be silently dropped.
    bad(lambda b, s: b["sourceByteEvidence"]["components"][5][
        "possibleSources"][0].update(sourceId="SQLITE3MC_LIB_2.4.0"))
    bad(lambda b, s: b["embeddedFiles"][5].update(
        name="SQLitePCLRaw.batteries_v3.dll"))
    bad(lambda b, s: b["sourceByteEvidence"]["components"][0][
        "possibleSources"].append(dict(sourceId="SQLITE3MC_LIB_2.4.0",
                                      relativePath="System.Private.CoreLib.dll")))
    bad(lambda b, s: b["sourceByteEvidence"]["components"][0].update(
        status="UNMATCHED_REQUIRES_REVIEW", possibleSources=[]))
    bad(lambda b, s: s.update(completeness="COMPLETE"))
    bad(lambda b, s: s["sourceTexts"].append(s["sourceTexts"][0]))
    bad(lambda b, s: s["sourceTexts"][0].update(sha256="bad"))
    print("MYTAGSYNC_NOTICE_COVERAGE_MATRIX_SELFTEST=PASS")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--self-test", action="store_true")
    p.add_argument("--bundle", type=Path)
    p.add_argument("--notices", type=Path)
    p.add_argument("--output", type=Path)
    a = p.parse_args()
    if a.self_test:
        selftest()
        return
    if a.bundle is None or a.notices is None or a.output is None:
        p.error("--bundle, --notices and --output are required")
    report = report_coverage(json.loads(a.bundle.read_text(encoding="utf-8")),
                             json.loads(a.notices.read_text(encoding="utf-8")))
    a.output.parent.mkdir(parents=True, exist_ok=True)
    a.output.write_text(json.dumps(report, indent=2, ensure_ascii=True) + "\n",
                        encoding="utf-8")
    print("MYTAGSYNC_NOTICE_COVERAGE_MATRIX=PASS "
          f"members={report['coveredMemberCount']} "
          f"captured_sources={report['noticeSourceFilesCaptured']} "
          f"missing_source_ids={len(report['missingNoticeSourceIds'])} "
          "completeness=NOT_ESTABLISHED distribution=REVIEW_REQUIRED_NOT_AUTHORIZED")


if __name__ == "__main__":
    try:
        main()
    except (CoverageError, ValueError, OSError, AssertionError) as exc:
        sys.exit("MYTAGSYNC_NOTICE_COVERAGE_MATRIX=FAIL " + str(exc))
