#!/usr/bin/env python3
"""Source-tagged Microsoft WPF/Windows native redist evidence, NEVER clearance.

Only the two original pinned Git source documents and the actual sealed
Windows bundle are checked. External live REDIST.TXT and Microsoft license
terms are linked as review leads, not reproduced or asserted satisfied.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import tempfile

BLOCKED = "REVIEW_REQUIRED_NOT_AUTHORIZED"
SOURCES = {
    "NET_WINDOWS_LICENSE_CLASSIFICATION": (
        "licenses/source-texts/dotnet-core-windows-license-map.md",
        "bbba73b9a0b553b1dffd2b4299fc5eb203e195fb",
        "dotnet/core:license-information-windows.md"),
    "WPF_10_0_13_RENAME_AND_REDIST": (
        "licenses/source-texts/WPF-v10.0.13-redistributables.md",
        "b90e9ce29b7500d3ebf83649cb552c0a6049b131",
        "dotnet/wpf@v10.0.13:Documentation/redistributables.md"),
}
NATIVE_LIBRARY = {
    "coreclr.dll", "clrjit.dll", "hostpolicy.dll", "hostfxr.dll",
    "presentationnative_cor3.dll", "vcruntime140_cor3.dll", "wpfgfx_cor3.dll",
}
COMPILER = "d3dcompiler_47_cor3.dll"
WINDOWS_PACKS = {
    "WINDOWSDESKTOP_PACK_10.0.13",
    "WINDOWSDESKTOP_RUNTIME_10.0.13",
}
LIBRARY_PACKS = WINDOWS_PACKS | {
    "NETCORE_PACK_10.0.13",
    "NETCORE_RUNTIME_10.0.13",
}
SHA_DIGITS = set("0123456789abcdef")


class ReviewError(ValueError):
    pass


def require(ok, message):
    if not ok:
        raise ReviewError(message)


def git_blob_id(data):
    return hashlib.sha1(b"blob " + str(len(data)).encode("ascii") + b"\x00" + data).hexdigest()


def valid_sha(h):
    return isinstance(h, str) and len(h) == 64 and set(h) <= SHA_DIGITS


def freeze_sources(root):
    frozen = []
    texts = {}
    for source_id, (rel, blob_sha, upstream) in sorted(SOURCES.items()):
        path = Path(root) / rel
        require(not path.is_symlink() and path.is_file(),
                "pinned Microsoft original document missing or symlinked")
        data = path.read_bytes()
        require(200 < len(data) < 32_000 and b"\x00" not in data,
                "Microsoft documentation has invalid content/length")
        require(git_blob_id(data) == blob_sha,
                "original Microsoft WPF/runtime license attribution Git blob changed")
        text = data.decode("utf-8-sig", "strict").casefold()
        texts[source_id] = text
        frozen.append(dict(sourceId=source_id, stagedName=Path(rel).name,
                           upstream=upstream, gitBlobSha1=blob_sha,
                           sha256=hashlib.sha256(data).hexdigest(),
                           bytes=len(data), status="SOURCE_FACTS_ONLY"))
    mapping = texts["NET_WINDOWS_LICENSE_CLASSIFICATION"]
    wpf = texts["WPF_10_0_13_RENAME_AND_REDIST"]
    for required in ("coreclr.dll", "wpfgfx_cor3.dll",
                     "presentationnative_cor3.dll", "vcruntime140_cor3.dll",
                     "d3dcompiler_47_cor3.dll", "windows sdk license",
                     ".net library license"):
        require(required in mapping,
                "official .NET Windows native file license map is incomplete")
    for required in ("d3dcompiler_47.dll", "d3dcompiler_47_cor3.dll",
                     "redistributable", "rename"):
        require(required in wpf, "pinned WPF 10.0.13 redist/rename evidence absent")
    return frozen


def inspect(bundle, originals):
    require(bundle.get("distributionLicenseStatus") == BLOCKED,
            "source inventory incorrectly lifts distribution hold")
    src = bundle.get("sourceByteEvidence", {})
    require(src.get("status") == BLOCKED and isinstance(src.get("components"), list),
            "source-byte proof missing")
    items = bundle.get("embeddedFiles", []) + bundle.get("externalFiles", [])
    require(len(items) == bundle.get("embeddedFileCount", -1) +
            bundle.get("externalFileCount", -1), "bundle member count mismatch")
    rows = src["components"]
    require(len(rows) == len(items), "incomplete member source evidence")
    indexed = {}
    for i in items:
        key = i["name"].casefold()
        require(key not in indexed and "/" not in key and "\\" not in key
                if key == COMPILER else key not in indexed,
                "duplicated/unsafe SDK D3DCompiler member")
        indexed[key] = i
    exact = {}
    for i in rows:
        name = i["name"].casefold()
        require(name not in exact and name in indexed,
                "missing/duplicate component origin")
        exact[name] = i
    require(set(exact) == set(indexed), "orphan sealed binary or origin record")
    require(COMPILER in indexed, "WindowsDesktop SDK redist DLL not in sealed bundle")
    compiler = indexed[COMPILER]
    source = exact[COMPILER]
    require(compiler["licenseReview"] == "MICROSOFT_WINDOWS_SDK_LICENSE_REVIEW"
            and source["licenseReview"] == compiler["licenseReview"],
            "D3DCompiler classified under wrong legal family")
    require(source["status"] == "MATCHED_EXACT_RAW_BYTES",
            "SDK DLL has no exact source bytes")
    origin_labels = sorted({c["sourceId"] for c in source.get("possibleSources", [])})
    require(origin_labels and set(origin_labels) <= WINDOWS_PACKS,
            "SDK DLL came from unqualified package/root")
    sdk_sha = compiler.get("onDiskSha256", compiler.get("sha256"))
    require(valid_sha(sdk_sha), "no SHA-256 for actual sealed D3DCompiler")
    natives = []
    for filename in sorted(NATIVE_LIBRARY):
        if filename not in indexed:
            continue
        item = indexed[filename]
        origin = exact[filename]
        require(item["licenseReview"] == "MICROSOFT_DOTNET_LIBRARY_LICENSE_REVIEW"
                and origin["licenseReview"] == item["licenseReview"]
                and origin["status"] == "MATCHED_EXACT_RAW_BYTES",
                "Windows native .NET Library file incorrectly treated as MIT")
        labels = sorted({c["sourceId"] for c in origin.get("possibleSources", [])})
        require(labels and set(labels) <= LIBRARY_PACKS,
                "Windows .NET Library native member has unexpected source")
        digest = item.get("onDiskSha256", item.get("sha256"))
        require(valid_sha(digest), "no sealed SHA-256 for .NET Library native DLL")
        natives.append(dict(name=item["name"], sealedSha256=digest, pinnedSources=labels))
    # Single-file .NET 10 can carry the native CLR runtime as prelinked
    # host payload rather than a separately manifest-indexed coreclr.dll.
    # Never infer that this eliminates .NET Library terms: the report's
    # container-wide singleFileHostLicenseTerms explicitly remains pending.
    # Enforce the WPF renderer/interop evidence that DOES occur as individual
    # FileEntries, and check every listed Library-native module's actual
    # byte-origin and license family above. Other sealed foreign modules are
    # already independently rejected by the comprehensive bundle auditor.
    observed_native = {v["name"].casefold() for v in natives}
    require(bool(observed_native & {"wpfgfx_cor3.dll",
                                   "presentationnative_cor3.dll"}),
            "no source-attested native WPF renderer/interop; observed=" +
            ",".join(sorted(observed_native)))
    return dict(schemaVersion=1,
                observedBundleMembers=len(items),
                originEvidence=originals,
                windowsSdkBinary=dict(name=compiler["name"],
                    sealedSha256=sdk_sha, pinnedSources=origin_labels,
                    upstreamOriginal="d3dcompiler_47.dll",
                    wpfActual="D3DCompiler_47_cor3.dll",
                    sourceCodeRelationship="UPSTREAM_WPF_VERSION_TAGGED_RENAME_CONFIRMED",
                    originalRedistList="https://learn.microsoft.com/en-us/legal/windows-sdk/redist",
                    licenseTerms="https://learn.microsoft.com/en-us/legal/windows-sdk/license",
                    listAndTermsCompliance="MANUAL_REDIST_AND_END_USER_REVIEW_PENDING"),
                netLibraryBinaryExceptions=natives,
                singleFileHostLicenseTerms="DOTNET_LIBRARY_MANUAL_END_USER_REVIEW_PENDING",
                termsUrl="https://dotnet.microsoft.com/en-us/dotnet_library_license.htm",
                missingFullLicenseSourceIds=["MICROSOFT_DOTNET_LIBRARY_LICENSE",
                                             "WINDOWS_SDK_D3DCOMPILER_LICENSE"],
                copyrightAndEndUserTerms="HUMAN_REVIEW_PENDING",
                completeness="NOT_ESTABLISHED",
                distributionLicenseStatus=BLOCKED)


def selftest(root):
    original = freeze_sources(root)
    names = sorted(NATIVE_LIBRARY | {COMPILER})
    members = []
    proofs = []
    for name in names:
        is_sdk = name == COMPILER
        cat = ("MICROSOFT_WINDOWS_SDK_LICENSE_REVIEW" if is_sdk
               else "MICROSOFT_DOTNET_LIBRARY_LICENSE_REVIEW")
        sid = ("WINDOWSDESKTOP_PACK_10.0.13" if is_sdk
               else "NETCORE_PACK_10.0.13")
        members.append(dict(name=name, licenseReview=cat, onDiskSha256="a" * 64))
        proofs.append(dict(name=name, licenseReview=cat,
                          status="MATCHED_EXACT_RAW_BYTES",
                          possibleSources=[dict(sourceId=sid, relativePath=name)]))
    bundle = dict(embeddedFiles=members, externalFiles=[],
                  embeddedFileCount=len(members), externalFileCount=0,
                  distributionLicenseStatus=BLOCKED,
                  sourceByteEvidence=dict(status=BLOCKED, components=proofs))
    assert inspect(bundle, original)["windowsSdkBinary"]["name"] == COMPILER
    assert inspect(bundle, original)["completeness"] == "NOT_ESTABLISHED"
    # Explicitly prove that the optional PresentationNative member may be
    # absent from the actual (trimmed/version-specific) publish manifest,
    # provided coreclr and a WPF renderer remain exact-source verified.
    optional = json.loads(json.dumps(bundle))
    optional["embeddedFiles"] = [x for x in optional["embeddedFiles"]
                                  if x["name"] != "presentationnative_cor3.dll"]
    optional["sourceByteEvidence"]["components"] = [
        x for x in optional["sourceByteEvidence"]["components"]
        if x["name"] != "presentationnative_cor3.dll"]
    optional["embeddedFileCount"] -= 1
    assert inspect(optional, original)["distributionLicenseStatus"] == BLOCKED
    # The real Windows single-file manifest does not expose a separate
    # coreclr.dll, even though core runtime / host distribution rights still
    # apply to the packaged executable. This is not a missing-license waiver.
    prelinked = json.loads(json.dumps(bundle))
    prelinked["embeddedFiles"] = [x for x in prelinked["embeddedFiles"]
                                   if x["name"] != "coreclr.dll"]
    prelinked["sourceByteEvidence"]["components"] = [
        x for x in prelinked["sourceByteEvidence"]["components"]
        if x["name"] != "coreclr.dll"]
    prelinked["embeddedFileCount"] -= 1
    report = inspect(prelinked, original)
    assert report["singleFileHostLicenseTerms"] == (
        "DOTNET_LIBRARY_MANUAL_END_USER_REVIEW_PENDING")
    assert report["distributionLicenseStatus"] == BLOCKED
    # Absence of ALL native WPF renderer/interop is a real negative case.
    optional["embeddedFiles"] = [x for x in optional["embeddedFiles"]
                                  if x["name"] != "wpfgfx_cor3.dll"]
    optional["sourceByteEvidence"]["components"] = [
        x for x in optional["sourceByteEvidence"]["components"]
        if x["name"] != "wpfgfx_cor3.dll"]
    optional["embeddedFileCount"] -= 1
    try:
        inspect(optional, original)
    except ReviewError:
        pass
    else:
        raise AssertionError("missing all WPF native members falsely accepted")
    def reject(mutator):
        broken = json.loads(json.dumps(bundle))
        mutator(broken)
        try:
            inspect(broken, original)
        except ReviewError:
            return
        raise AssertionError("untrusted .NET/SDK license evidence was accepted")
    reject(lambda b: b["embeddedFiles"].pop())
    reject(lambda b: b["embeddedFiles"][-1].update(
        licenseReview="MICROSOFT_COMPONENT_LICENSE_REVIEW"))
    # A source-origin ledger that disagrees with the sealed DLL's legal
    # family is not valid provenance, even when its raw bytes/source ID match.
    # This regression used to pass because only the sealed member's category
    # was checked for native .NET Library exceptions.
    reject(lambda b: next(x for x in b["sourceByteEvidence"]["components"]
                          if x["name"] == "wpfgfx_cor3.dll").update(
        licenseReview="MICROSOFT_WINDOWS_SDK_LICENSE_REVIEW"))
    reject(lambda b: b["sourceByteEvidence"]["components"][0].update(
        status="UNMATCHED_REQUIRES_REVIEW"))
    reject(lambda b: b["sourceByteEvidence"]["components"][0]["possibleSources"][0].update(
        sourceId="UNKNOWN_FOREIGN_PACK"))
    reject(lambda b: b.update(distributionLicenseStatus="DISTRIBUTION_APPROVED"))
    reject(lambda b: b["embeddedFiles"][0].update(onDiskSha256="not-sha"))
    with tempfile.TemporaryDirectory(prefix="mytagsync-ms-evidence-") as temp:
        for _, (rel, _, _) in SOURCES.items():
            target = Path(temp) / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((Path(root) / rel).read_bytes())
        file = Path(temp) / SOURCES["WPF_10_0_13_RENAME_AND_REDIST"][0]
        file.write_bytes(file.read_bytes() + b"INVALID_DOC_CHANGE")
        try:
            freeze_sources(temp)
        except ReviewError:
            pass
        else:
            raise AssertionError("mutated Microsoft upstream Git blob was accepted")
    print("MYTAGSYNC_MS_NATIVE_REDIST_EVIDENCE_SELFTEST=PASS")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--self-test", action="store_true")
    p.add_argument("--source-root", type=Path, default=Path("."))
    p.add_argument("--bundle", type=Path)
    p.add_argument("--output", type=Path)
    args = p.parse_args()
    if args.self_test:
        selftest(args.source_root)
        return
    if not args.bundle or not args.output:
        p.error("--bundle and --output are required")
    report = inspect(json.loads(args.bundle.read_text(encoding="utf-8-sig")),
                     freeze_sources(args.source_root))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("MYTAGSYNC_MS_NATIVE_REDIST_EVIDENCE=PASS "
          f"sealed={report['observedBundleMembers']} "
          f"native_library={len(report['netLibraryBinaryExceptions'])} "
          "terms=HUMAN_REVIEW_PENDING distribution=REVIEW_REQUIRED_NOT_AUTHORIZED")


if __name__ == "__main__":
    try:
        main()
    except (ReviewError, ValueError, KeyError, UnicodeDecodeError, OSError,
            AssertionError) as exc:
        sys.exit("MYTAGSYNC_MS_NATIVE_REDIST_EVIDENCE=FAIL " + str(exc))
