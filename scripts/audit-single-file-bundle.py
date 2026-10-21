#!/usr/bin/env python3
"""Inventory ACTUAL embedded .NET 6+ single-file bytes, not NuGet guesses.

Manifest format: Microsoft.NET.HostModel.Bundle.Manifest/FileEntry (Microsoft
dotnet/runtime). Output omits original absolute paths. This report NEVER
licenses binary distribution or replaces matching source-package notices.
"""
import argparse
from collections import Counter
import hashlib
import json
import mmap
import os
import re
from pathlib import Path
import struct
import sys
import tempfile

SIGNATURE = bytes.fromhex(
    "8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae")
DOTNET_NATIVE = {
    "coreclr.dll", "clrjit.dll", "hostpolicy.dll", "hostfxr.dll",
    "presentationnative_cor3.dll", "vcruntime140_cor3.dll", "wpfgfx_cor3.dll"}
SDK_NATIVE = {"d3dcompiler_47_cor3.dll"}
MEDIA = {".mp3", ".flac", ".cue", ".wav", ".opus", ".wv", ".aiff", ".m4a"}
# These are exact *names observed in the frozen WindowsDesktop 10.0.13
# portable bundle*. Attribution hints, NOT verified ownership or license.
WPF_MAIN = {
    "accessibility.dll", "directwriteforwarder.dll", "penimc_cor3.dll",
    "presentationcore.dll", "presentationframework.dll",
    "presentationframework-systemcore.dll",
    "presentationframework-systemdata.dll",
    "presentationframework-systemdrawing.dll",
    "presentationframework-systemxml.dll",
    "presentationframework-systemxmllinq.dll",
    "presentationframework.aero.dll", "presentationframework.aero2.dll",
    "presentationframework.aerolite.dll",
    "presentationframework.classic.dll", "presentationframework.fluent.dll",
    "presentationframework.luna.dll", "presentationframework.royale.dll",
    "presentationui.dll", "reachframework.dll", "uiautomationclient.dll",
    "uiautomationclientsideproviders.dll", "uiautomationprovider.dll",
    "uiautomationtypes.dll", "windowsbase.dll",
}
WPF_SATELLITE_BASES = {
    "presentationcore", "presentationframework", "presentationui",
    "reachframework", "uiautomationclient",
    "uiautomationclientsideproviders", "uiautomationprovider",
    "uiautomationtypes", "windowsbase",
}
WPF_LOCALES = {
    "cs", "de", "es", "fr", "it", "ja", "ko", "pl", "pt-br",
    "ru", "tr", "zh-hans", "zh-hant",
}
NET_SHIMS = {"mscorlib.dll", "netstandard.dll"}
SQLITEPCLRAW_DLLS = {"sqlitepclraw.batteries_v2.dll", "sqlitepclraw.core.dll"}

class BundleError(ValueError):
    pass

def number(mm, fmt, pos):
    size = struct.calcsize(fmt)
    if pos + size > len(mm):
        raise BundleError("truncated bundle manifest")
    return struct.unpack_from(fmt, mm, pos), pos + size

def read_string(mm, pos):
    length = 0
    for i in range(5):
        if pos >= len(mm):
            raise BundleError("truncated bundle string length")
        value = mm[pos]
        pos += 1
        length |= (value & 127) << (i * 7)
        if value & 128 == 0:
            break
    else:
        raise BundleError("invalid seven-bit bundle length")
    if length > 8192 or pos + length > len(mm):
        raise BundleError("oversized/truncated bundle string")
    try:
        return bytes(mm[pos:pos+length]).decode("utf-8", "strict"), pos + length
    except UnicodeDecodeError as e:
        raise BundleError("invalid UTF-8 bundle name") from e

def category(path):
    base = path.rsplit("/", 1)[-1].casefold()
    if base in DOTNET_NATIVE:
        return "MICROSOFT_DOTNET_LIBRARY_LICENSE_REVIEW"
    if base in SDK_NATIVE:
        return "MICROSOFT_WINDOWS_SDK_LICENSE_REVIEW"
    if "sqlite3mc" in base:
        return "THIRD_PARTY_SQLITE3MC_LICENSE_REVIEW"
    if base in SQLITEPCLRAW_DLLS:
        return "LOCKED_NUGET_SQLITEPCLRAW_ATTRIBUTION_PENDING"
    if base in NET_SHIMS:
        return "MICROSOFT_NET_RUNTIME_ATTRIBUTION_PENDING"
    if base in WPF_MAIN:
        return "MICROSOFT_WPF_PACK_ATTRIBUTION_PENDING"
    parts = path.casefold().split("/")
    if (len(parts) == 2 and parts[0] in WPF_LOCALES and
        parts[1].endswith(".resources.dll") and
        parts[1][:-len(".resources.dll")] in WPF_SATELLITE_BASES):
        return "MICROSOFT_WPF_SATELLITE_ATTRIBUTION_PENDING"
    if base.startswith(("microsoft.", "system.")):
        return "MICROSOFT_COMPONENT_LICENSE_REVIEW"
    if base.endswith((".deps.json", ".runtimeconfig.json")):
        return "APP_METADATA"
    if base.startswith("rekordbox mytagsync"):
        return "APPLICATION_OWNERSHIP_REVIEW"
    return "UNCLASSIFIED_REQUIRES_REVIEW"

def inspect(path, publish_dir=None):
    sidecars = []
    if publish_dir is not None:
        root = Path(publish_dir).resolve()
        if not root.is_dir() or Path(path).resolve().parent != root:
            raise BundleError("invalid publish directory for executable")
        for f in sorted(root.rglob("*")):
            if not f.is_file() or f.resolve() == Path(path).resolve():
                continue
            relative = f.relative_to(root).as_posix()
            if (relative.startswith(("/", "../")) or
                any(part in ("", ".", "..") for part in relative.split("/")) or
                f.suffix.lower() in MEDIA or
                f.suffix.lower() in (".db", ".edb", ".key")):
                raise BundleError("unsafe external published companion")
            sidecars.append(dict(name=relative, bytes=f.stat().st_size,
                                 sha256=hashlib.sha256(f.read_bytes()).hexdigest(),
                                 licenseReview=category(relative)))
    with Path(path).open("rb") as fp, mmap.mmap(
            fp.fileno(), 0, access=mmap.ACCESS_READ) as mm:
        pos = mm.find(SIGNATURE)
        if pos < 8 or mm.find(SIGNATURE, pos + 1) != -1:
            raise BundleError("missing or ambiguous .NET bundle signature")
        (offset,), _ = number(mm, "<q", pos - 8)
        if offset < pos + len(SIGNATURE) or offset >= len(mm):
            raise BundleError("invalid .NET bundle header offset")
        (major, minor, count), index = number(mm, "<IIi", offset)
        if not 6 <= major <= 10 or not 0 < count <= 10000:
            raise BundleError("unsupported bundle manifest/version/count")
        bundle_id, index = read_string(mm, index)
        if not 0 < len(bundle_id) <= 100:
            raise BundleError("missing bundle ID")
        _, index = number(mm, "<qqqqQ", index)  # deps/runtime config and flags
        files, seen, names, spans = [], set(), set(), []
        for _ in range(count):
            (start, size, compressed, kind), index = number(mm, "<qqqB", index)
            name, index = read_string(mm, index)
            canonical = name.casefold()
            if (not name or name.startswith(("/", "\\")) or
                "\\" in name or ":" in name or
                any(x in ("", ".", "..") for x in name.split("/")) or
                canonical in seen or
                Path(name).suffix.lower() in MEDIA or kind > 5 or
                size <= 0 or start <= 0 or compressed < 0 or
                compressed > size or start + (compressed or size) > offset):
                raise BundleError("unsafe/duplicate/invalid embedded file")
            on_disk_bytes = compressed or size
            end = start + on_disk_bytes
            if any(start < prior_end and prior_start < end
                   for prior_start, prior_end in spans):
                raise BundleError("overlapping embedded bundle payloads")
            spans.append((start, end))
            # Hash the exact sealed bytes, not a guessed extracted assembly.
            # Compressed payloads are hashed as stored in the executable;
            # this is not a signature or a proof of redistribution rights.
            stored_sha = hashlib.sha256(memoryview(mm)[start:end]).hexdigest()
            seen.add(canonical)
            names.add(name.rsplit("/", 1)[-1].casefold())
            files.append(dict(name=name, fileType=kind, bytes=size,
                              compressedBytes=compressed,
                              onDiskBytes=on_disk_bytes,
                              onDiskEncoding=("COMPRESSED" if compressed else "RAW"),
                              onDiskSha256=stored_sha,
                              licenseReview=category(name)))
        # Direct proof of the product's advertised native WindowsDesktop and
        # SQLCipher bundle members, not a license-approval shortcut.
        all_names = names | {x["name"].rsplit("/", 1)[-1].casefold()
                             for x in sidecars}
        # Some WindowsDesktop components can be loose sidecars. The bundle
        # manifest alone must not be mistaken for the entire publish folder.
        # In modern .NET single-file Windows publishing, CoreCLR's native
        # runtime may be prelinked into the host and not a manifest file.
        # System.Private.CoreLib is the core managed runtime identity. This
        # is component inventory evidence, NOT full portable-runtime proof.
        if "system.private.corelib.dll" not in all_names:
            evidence = sorted(all_names)
            raise BundleError(
                "expected .NET CoreLib runtime payload missing; "
                f"embedded={len(names)} external={len(sidecars)} "
                f"actual-component-names-first60={','.join(evidence[:60])}")
        if not all_names.intersection({
                "wpfgfx_cor3.dll", "presentationcore.dll",
                "presentationframework.dll"}):
            # Retain the previous main's bounded native FileType=2
            # diagnostics; never downgrade a missing WPF runtime to PASS.
            native = sorted(f["name"] for f in files
                            if f["fileType"] == 2
                            and f["name"].lower().endswith(".dll"))
            raise BundleError(
                "expected WPF managed/native component is missing; "
                f"native_dll_count={len(native)}, "
                "native_dll_sample=" + repr(native[:24]))
        if not any("sqlite3mc" in name for name in all_names):
            raise BundleError("expected SQLite3MC runtime component is missing")
        return dict(schemaVersion=1, bundleMajorVersion=major,
                    bundleMinorVersion=minor, embeddedFileCount=count,
                    bundleSha256=hashlib.sha256(mm).hexdigest(),
                    distributionLicenseStatus="REVIEW_REQUIRED_NOT_AUTHORIZED",
                    externalFileCount=len(sidecars),
                    externalFiles=sidecars,
                    embeddedFiles=sorted(files, key=lambda f: f["name"].casefold()))


def correlate_exact_sources(inventory, source_roots):
    """Only exact RAW stored bytes equal to a pinned source file establish an
    origin candidate. No filenames-only approvals, decompression assumptions,
    source absolute paths, or redistribution/license clearance.
    """
    verified = {}
    seen_ids = set()
    for definition in source_roots:
        if "=" not in definition:
            raise BundleError("source root must be ID=directory")
        origin, folder = definition.split("=", 1)
        if not re.fullmatch(r"[A-Z0-9][A-Z0-9._-]{2,100}", origin) or origin in seen_ids:
            raise BundleError("invalid or duplicated pinned source ID")
        seen_ids.add(origin)
        root = Path(folder)
        if root.is_symlink() or not root.is_dir():
            raise BundleError("pinned source root unavailable: " + origin)
        root = root.resolve(strict=True)
        processed = 0
        for parent, dirs, names in os.walk(root, followlinks=False):
            dirs[:] = sorted(d for d in dirs if not (Path(parent) / d).is_symlink())
            for filename in sorted(names):
                file = Path(parent) / filename
                if file.is_symlink() or file.suffix.lower() not in {
                        ".dll", ".exe", ".json", ".winmd", ".dat"}:
                    continue
                processed += 1
                if processed > 10000:
                    raise BundleError("unbounded pinned source root: " + origin)
                if not 0 < file.stat().st_size <= 256 * 1024 * 1024:
                    continue
                relative = file.relative_to(root).as_posix()
                if any(part in ("", ".", "..") for part in relative.split("/")):
                    raise BundleError("invalid source-relative file path")
                checksum = hashlib.sha256()
                with file.open("rb") as stream:
                    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                        checksum.update(chunk)
                key = (filename.casefold(), file.stat().st_size, checksum.hexdigest())
                verified.setdefault(key, set()).add((origin, relative))

    counts = {"MATCHED_EXACT_RAW_BYTES": 0, "COMPRESSED_UNRESOLVED": 0,
              "UNMATCHED_REQUIRES_REVIEW": 0}
    evidence = []
    both = [(x, False) for x in inventory["embeddedFiles"]] + [
        (x, True) for x in inventory["externalFiles"]]
    for item, external in both:
        mode = "RAW" if external else item["onDiskEncoding"]
        size = item["bytes"] if external else item["onDiskBytes"]
        checksum = item["sha256"] if external else item["onDiskSha256"]
        candidates = (sorted(verified.get((
            item["name"].rsplit("/", 1)[-1].casefold(), size, checksum), ()))
            if mode == "RAW" else [])
        decision = ("MATCHED_EXACT_RAW_BYTES" if candidates else
                    "COMPRESSED_UNRESOLVED" if mode == "COMPRESSED" else
                    "UNMATCHED_REQUIRES_REVIEW")
        counts[decision] += 1
        evidence.append(dict(name=item["name"], status=decision,
                             licenseReview=item["licenseReview"],
                             possibleSources=[dict(sourceId=i, relativePath=p)
                                              for i, p in candidates]))
    evidence.sort(key=lambda x: x["name"].casefold())
    return dict(status="REVIEW_REQUIRED_NOT_AUTHORIZED",
                scope="STORED_RAW_BYTES_ONLY_NO_LICENSE_OR_COPYRIGHT_ASSERTION",
                pinnedSourceIds=sorted(seen_ids),
                counts=counts, components=evidence)


EXPECTED_APPLICATION_OUTPUTS = {
    "rekordbox MyTagSync.dll": "APPLICATION_OWNERSHIP_REVIEW",
    "rekordbox MyTagSync.deps.json": "APP_METADATA",
    "rekordbox MyTagSync.runtimeconfig.json": "APP_METADATA",
}


def audit_remaining_application_outputs(inventory):
    """Only the THREE observed self-produced outputs may lack a pinned package
    byte match. Record their stored hashes, never infer license clearance.
    Every other unmatched/foreign member is a build-stopping regression.
    """
    members = {x["name"]: x for x in
               inventory["embeddedFiles"] + inventory["externalFiles"]}
    evidence = inventory["sourceByteEvidence"]["components"]
    all_evidence = {x["name"]: x for x in evidence}
    if len(all_evidence) != len(evidence) or set(all_evidence) != set(members):
        raise BundleError("source origin inventory does not cover sealed members")
    attested = []
    for name, category_name in sorted(EXPECTED_APPLICATION_OUTPUTS.items()):
        item = members.get(name)
        row = all_evidence.get(name)
        if item is None or row is None or item["licenseReview"] != category_name:
            raise BundleError("required application output identity is missing/altered: " + name)
        if row["licenseReview"] != category_name or row["status"] == "COMPRESSED_UNRESOLVED":
            raise BundleError("unsafe application output evidence: " + name)
        if row["status"] not in ("MATCHED_EXACT_RAW_BYTES", "UNMATCHED_REQUIRES_REVIEW"):
            raise BundleError("unexpected application source match status: " + name)
        attested.append(dict(name=name, sha256=(
            item["sha256"] if "sha256" in item else item["onDiskSha256"]),
            sourceStatus=row["status"], category=category_name))
    unexpected = sorted(x["name"] for x in evidence if
                        x["status"] != "MATCHED_EXACT_RAW_BYTES"
                        and x["name"] not in EXPECTED_APPLICATION_OUTPUTS)
    if unexpected:
        raise BundleError("unexpected third-party/unrecognized unmatched sealed members: "
                          + ",".join(unexpected))
    return dict(status="APPLICATION_OUTPUT_IDENTITY_REVIEW_REQUIRED",
                distributionLicenseStatus="REVIEW_REQUIRED_NOT_AUTHORIZED",
                inputsCompared="SEALED_BYTES_TO_PINNED_SOURCE_AND_EXACT_APP_OUTPUT_NAMES",
                applicationOutputs=attested,
                unexpectedUnmatchedCount=0)


def seven(n):
    buf = bytearray()
    while n >= 128:
        buf.append((n & 127) | 128)
        n >>= 7
    buf.append(n)
    return bytes(buf)

def fixture(paths, invalid_offset=False, overlap=False):
    fake = bytearray(b"MZ" + bytes(200))
    spans = []
    for name in paths:
        at = len(fake)
        fake += b"FICTIONAL_SYNTHETIC"
        spans.append((at, 19, name))
    marker = len(fake)
    fake += bytes(8) + SIGNATURE
    header = len(fake)
    fake += struct.pack("<IIi", 6, 0, len(paths))
    id_bytes = b"synthetic-id"
    fake += seven(len(id_bytes)) + id_bytes
    fake += struct.pack("<qqqqQ", 0, 0, 0, 0, 0)
    for i, (start, size, name) in enumerate(spans):
        if invalid_offset and i == 0:
            start = 999999999
        if overlap and i == 1:
            start = spans[0][0]
        encoded = name.encode("utf-8")
        fake += struct.pack("<qqqB", start, size, 0, 1)
        fake += seven(len(encoded)) + encoded
    struct.pack_into("<q", fake, marker, header)
    return fake

def selftest():
    base = ["System.Private.CoreLib.dll", "wpfgfx_cor3.dll",
            "D3DCompiler_47_cor3.dll", "e_sqlite3mc.dll",
            "rekordbox MyTagSync.dll"]
    with (tempfile.TemporaryDirectory(prefix="mytagsync-bundle-test-") as root,
          tempfile.TemporaryDirectory(prefix="mytagsync-pinned-source-") as source_tmp):
        path = Path(root) / "invented.exe"
        path.write_bytes(fixture(base))
        obj = inspect(path)
        assert obj["embeddedFileCount"] == 5
        assert obj["distributionLicenseStatus"] == "REVIEW_REQUIRED_NOT_AUTHORIZED"
        # Same-basename byte equality against a pinned source is evidence;
        # equal bytes with another basename and compressed members are not.
        source_root = Path(source_tmp)
        (source_root / "System.Private.CoreLib.dll").write_bytes(
            b"FICTIONAL_SYNTHETIC")
        (source_root / "Unrelated.dll").write_bytes(b"FICTIONAL_SYNTHETIC")
        evidence = correlate_exact_sources(
            obj, ["NETCORE_RUNTIME_10.0.13=" + str(source_root)])
        assert evidence["counts"]["MATCHED_EXACT_RAW_BYTES"] == 1
        assert evidence["counts"]["UNMATCHED_REQUIRES_REVIEW"] == 4
        gaps = [x for x in evidence["components"]
                if x["status"] != "MATCHED_EXACT_RAW_BYTES"]
        assert {x["name"] for x in gaps} == set(base[1:])
        assert all(x["licenseReview"] != "DISTRIBUTION_APPROVED" for x in gaps)
        core = next(x for x in evidence["components"]
                    if x["name"] == "System.Private.CoreLib.dll")
        assert core["possibleSources"] == [
            dict(sourceId="NETCORE_RUNTIME_10.0.13",
                 relativePath="System.Private.CoreLib.dll")]
        compressed = json.loads(json.dumps(obj))
        compressed["embeddedFiles"][0]["onDiskEncoding"] = "COMPRESSED"
        assert correlate_exact_sources(
            compressed, ["NETCORE_RUNTIME_10.0.13=" + str(source_root)]
        )["counts"]["COMPRESSED_UNRESOLVED"] == 1
        (source_root / "System.Private.CoreLib.dll").write_bytes(b"CHANGED")
        assert correlate_exact_sources(
            obj, ["NETCORE_RUNTIME_10.0.13=" + str(source_root)]
        )["counts"]["MATCHED_EXACT_RAW_BYTES"] == 0
        # Synthetic positive: pinned third-party code and only the three
        # own, deterministic app outputs stay unmatched, never "licensed".
        expected = dict(
            embeddedFiles=[
                dict(name=name, licenseReview=category_name,
                     onDiskSha256="a" * 64)
                for name, category_name in EXPECTED_APPLICATION_OUTPUTS.items()
            ],
            externalFiles=[],
        )
        expected["sourceByteEvidence"] = dict(components=[
            dict(name=x["name"], licenseReview=x["licenseReview"],
                 status="UNMATCHED_REQUIRES_REVIEW")
            for x in expected["embeddedFiles"]
        ])
        attested = audit_remaining_application_outputs(expected)
        assert attested["unexpectedUnmatchedCount"] == 0
        assert attested["distributionLicenseStatus"] == "REVIEW_REQUIRED_NOT_AUTHORIZED"
        assert len(attested["applicationOutputs"]) == 3
        # A missing/renamed output, altered ownership category, compressed
        # app member, or unmatched unknown vendor binary must fail closed.
        for altered in ("missing", "wrong_owner", "compressed", "foreign"):
            sample = json.loads(json.dumps(expected))
            if altered == "missing":
                sample["embeddedFiles"].pop()
            elif altered == "wrong_owner":
                sample["embeddedFiles"][0]["licenseReview"] = "UNKNOWN_OWNER"
            elif altered == "compressed":
                sample["sourceByteEvidence"]["components"][0]["status"] = (
                    "COMPRESSED_UNRESOLVED")
            else:
                sample["embeddedFiles"].append(
                    dict(name="VendorUnknown.dll",
                         licenseReview="UNCLASSIFIED_REQUIRES_REVIEW",
                         onDiskSha256="b" * 64))
                sample["sourceByteEvidence"]["components"].append(
                    dict(name="VendorUnknown.dll",
                         licenseReview="UNCLASSIFIED_REQUIRES_REVIEW",
                         status="UNMATCHED_REQUIRES_REVIEW"))
            try:
                audit_remaining_application_outputs(sample)
            except BundleError:
                pass
            else:
                raise AssertionError("unsafe unaccounted-for sealed member passed")

        for invalid in ["badid=" + str(source_root),
                        "NETCORE_RUNTIME_10.0.13=/nonexistent/pinned-source"]:
            try:
                correlate_exact_sources(obj, [invalid])
            except BundleError:
                pass
            else:
                raise AssertionError("unsafe/absent pinned source accepted")

        assert category("de/PresentationCore.resources.dll") == (
            "MICROSOFT_WPF_SATELLITE_ATTRIBUTION_PENDING")
        assert category("PresentationFramework.Aero2.dll") == (
            "MICROSOFT_WPF_PACK_ATTRIBUTION_PENDING")
        assert category("mscorlib.dll") == (
            "MICROSOFT_NET_RUNTIME_ATTRIBUTION_PENDING")
        assert category("SQLitePCLRaw.core.dll") == (
            "LOCKED_NUGET_SQLITEPCLRAW_ATTRIBUTION_PENDING")
        for unknown in ("OtherVendor.dll", "de/Unknown.resources.dll",
                        "other/PresentationCore.resources.dll"):
            assert category(unknown) == "UNCLASSIFIED_REQUIRES_REVIEW"
        expected_sha = hashlib.sha256(b"FICTIONAL_SYNTHETIC").hexdigest()
        assert all(f["onDiskSha256"] == expected_sha and
                   f["onDiskBytes"] == 19 and f["onDiskEncoding"] == "RAW"
                   for f in obj["embeddedFiles"])
        # Content mutation must alter the exact embedded component digest.
        changed = bytearray(fixture(base))
        changed[202] ^= 1
        path.write_bytes(changed)
        mutated = inspect(path)
        changed_member = next(f for f in mutated["embeddedFiles"]
                              if f["name"] == base[0])
        assert changed_member["onDiskSha256"] != expected_sha
        path.write_bytes(fixture(base))
        # Native WPF may legitimately be outside the single-file; a managed
        # WPF assembly is still sufficient evidence of WPF's presence.
        managed = [p for p in base if p != "wpfgfx_cor3.dll"] + ["PresentationCore.dll"]
        path.write_bytes(fixture(managed))
        inspect(path)
        assert any(f["licenseReview"] == "MICROSOFT_WINDOWS_SDK_LICENSE_REVIEW"
                   for f in obj["embeddedFiles"])
        # Real EXE+external folder: WPF component can be a loose sidecar.
        path.write_bytes(fixture([p for p in base if p != "wpfgfx_cor3.dll"]))
        sidecar = Path(root) / "PresentationCore.dll"
        sidecar.write_bytes(b"SYNTHETIC_WPF_COMPANION")
        actual = inspect(path, root)
        assert actual["externalFileCount"] == 1
        assert actual["externalFiles"][0]["name"] == "PresentationCore.dll"
        bad_media = Path(root) / "private_track.mp3"
        bad_media.write_bytes(b"NOT_REAL_MEDIA")
        try:
            inspect(path, root)
        except BundleError:
            pass
        else:
            raise AssertionError("media in portable publish folder was not blocked")
        bad_media.unlink()
        sidecar.unlink()
        # Preserve PR #71's fail-closed native FileType=2 diagnostics, now
        # with the qualified CoreLib/WPF-or-sidecar compatibility contract.
        path.write_bytes(fixture([p for p in base if p != "wpfgfx_cor3.dll"]))
        try:
            inspect(path)
        except BundleError as exc:
            assert "native_dll_count=" in str(exc)
            assert "WPF managed/native" in str(exc)
        else:
            raise AssertionError("missing WPF and no companion was accepted")
        attacks = [(base + ["SYSTEM.PRIVATE.CORELIB.DLL"], False, False),
                   (base + ["../fake.mp3"], False, False),
                   (base, True, False), (base, False, True),
                   (base[1:], False, False), ([], False, False)]
        for names, bad_offset, overlap in attacks:
            path.write_bytes(fixture(names, bad_offset, overlap))
            try:
                inspect(path)
            except BundleError:
                continue
            raise AssertionError("invalid synthetic bundle was accepted")
    print("MYTAGSYNC_REAL_BUNDLE_MANIFEST_AUDIT_SELFTEST=PASS")

def main():
    p = argparse.ArgumentParser()
    p.add_argument("--self-test", action="store_true")
    p.add_argument("--exe", type=Path)
    p.add_argument("--publish-dir", type=Path)
    p.add_argument("--output", type=Path)
    p.add_argument("--source-root", action="append", default=[])
    a = p.parse_args()
    if a.self_test:
        selftest()
        return
    if a.exe is None or a.output is None:
        p.error("--exe and --output are required")
    result = inspect(a.exe, a.publish_dir)
    if a.source_root:
        result["sourceByteEvidence"] = correlate_exact_sources(result, a.source_root)
        result["applicationOutputEvidence"] = audit_remaining_application_outputs(result)
        # Source origin is not equivalent to redistribution/license clearance.
        # These are sanitized original single-file manifest member names, not
        # machine/user paths. Surface exact unmatched members from the real
        # Windows publish so that follow-up investigations are falsifiable.
        unresolved = [component for component in
                      result["sourceByteEvidence"]["components"]
                      if component["status"] != "MATCHED_EXACT_RAW_BYTES"]
        print("MYTAGSYNC_SOURCE_BYTE_UNRESOLVED_COUNT=" + str(len(unresolved)))
        for component in unresolved:
            print("MYTAGSYNC_SOURCE_BYTE_UNRESOLVED_COMPONENT="
                  + json.dumps(dict(name=component["name"],
                                    sourceStatus=component["status"],
                                    licenseReview=component["licenseReview"]),
                               ensure_ascii=True, sort_keys=True))
    a.output.parent.mkdir(parents=True, exist_ok=True)
    a.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print("MYTAGSYNC_REAL_BUNDLE_MANIFEST_AUDIT=PASS "
          f"embedded={result['embeddedFileCount']} "
          f"external={result['externalFileCount']} "
          f"categories={dict(Counter(f['licenseReview'] for f in result['embeddedFiles']))} "
          "distribution=REVIEW_REQUIRED_NOT_AUTHORIZED")
    # Exact names of still-unclassified payloads are needed to attribute
    # copyrights/notices against the pinned SDK/runtime/NuGet pack. These
    # are sanitized bundle member names from this synthetic public build,
    # NOT user paths or extracted binaries. Never imply license approval.
    unresolved = sorted(
        (item["name"] for item in result["embeddedFiles"] + result["externalFiles"]
         if item["licenseReview"] == "UNCLASSIFIED_REQUIRES_REVIEW"),
        key=str.casefold)
    print("MYTAGSYNC_NOTICE_REVIEW_UNCLASSIFIED_COUNT=" + str(len(unresolved)))
    for name in unresolved:
        print("MYTAGSYNC_NOTICE_REVIEW_COMPONENT=" + json.dumps(name, ensure_ascii=True))

if __name__ == "__main__":
    try:
        main()
    except (BundleError, AssertionError, OSError) as exc:
        sys.exit("MYTAGSYNC_REAL_BUNDLE_MANIFEST_AUDIT=FAIL " + str(exc))
