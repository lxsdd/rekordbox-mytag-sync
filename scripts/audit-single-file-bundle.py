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
    if base.startswith(("microsoft.", "system.")):
        return "MICROSOFT_COMPONENT_LICENSE_REVIEW"
    if base.endswith((".deps.json", ".runtimeconfig.json")):
        return "APP_METADATA"
    if base.startswith("rekordbox mytagsync"):
        return "APPLICATION_OWNERSHIP_REVIEW"
    return "UNCLASSIFIED_REQUIRES_REVIEW"

def inspect(path):
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
        files, seen, names = [], set(), set()
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
            seen.add(canonical)
            names.add(name.rsplit("/", 1)[-1].casefold())
            files.append(dict(name=name, fileType=kind, bytes=size,
                              compressedBytes=compressed,
                              licenseReview=category(name)))
        # Never turn missing runtime members into a successful distribution
        # audit. Supply bounded, source-relative evidence for the actual
        # release payload so a Windows FULL regression can be repaired without
        # guessing which binary did (or did not) get embedded.
        required = {"coreclr.dll", "wpfgfx_cor3.dll"}
        missing = sorted(required - names)
        if missing:
            native = sorted(f["name"] for f in files
                            if f["fileType"] in (3, 4)
                            and f["name"].lower().endswith(".dll"))
            raise BundleError(
                "self-contained .NET/WindowsDesktop runtime missing: "
                + ",".join(missing)
                + f"; embedded_entries={count}, native_dll_count={len(native)}, "
                + "native_dll_sample=" + repr(native[:24]))
        if not any("sqlite3mc" in name for name in names):
            raise BundleError("expected native SQLite3MC library missing")
        return dict(schemaVersion=1, bundleMajorVersion=major,
                    bundleMinorVersion=minor, embeddedFileCount=count,
                    bundleSha256=hashlib.sha256(mm).hexdigest(),
                    distributionLicenseStatus="REVIEW_REQUIRED_NOT_AUTHORIZED",
                    embeddedFiles=sorted(files, key=lambda f: f["name"].casefold()))

def seven(n):
    buf = bytearray()
    while n >= 128:
        buf.append((n & 127) | 128)
        n >>= 7
    buf.append(n)
    return bytes(buf)

def fixture(paths, invalid_offset=False):
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
        encoded = name.encode("utf-8")
        fake += struct.pack("<qqqB", start, size, 0, 1)
        fake += seven(len(encoded)) + encoded
    struct.pack_into("<q", fake, marker, header)
    return fake

def selftest():
    base = ["coreclr.dll", "wpfgfx_cor3.dll", "D3DCompiler_47_cor3.dll",
            "e_sqlite3mc.dll", "rekordbox MyTagSync.dll"]
    with tempfile.TemporaryDirectory(prefix="mytagsync-bundle-test-") as root:
        path = Path(root) / "invented.exe"
        path.write_bytes(fixture(base))
        obj = inspect(path)
        assert obj["embeddedFileCount"] == 5
        assert obj["distributionLicenseStatus"] == "REVIEW_REQUIRED_NOT_AUTHORIZED"
        assert any(f["licenseReview"] == "MICROSOFT_WINDOWS_SDK_LICENSE_REVIEW"
                   for f in obj["embeddedFiles"])
        attacks = [(base + ["CORECLR.DLL"], False),
                   (base + ["../fake.mp3"], False),
                   (base, True), (base[:1] + base[2:], False),
                   ([], False)]
        for names, bad_offset in attacks:
            path.write_bytes(fixture(names, bad_offset))
            try:
                inspect(path)
            except BundleError:
                continue
            raise AssertionError("invalid synthetic bundle was accepted")
        # Diagnostic detail must never lower the positive embedded-runtime
        # requirement, even when other native and SQLCipher members exist.
        path.write_bytes(fixture(base[:1] + base[2:]))
        try:
            inspect(path)
        except BundleError as exc:
            assert "wpfgfx_cor3.dll" in str(exc)
            assert "native_dll_count=" in str(exc)
        else:
            raise AssertionError("missing WPF runtime passed as distributable")
    print("MYTAGSYNC_REAL_BUNDLE_MANIFEST_AUDIT_SELFTEST=PASS")

def main():
    p = argparse.ArgumentParser()
    p.add_argument("--self-test", action="store_true")
    p.add_argument("--exe", type=Path)
    p.add_argument("--output", type=Path)
    a = p.parse_args()
    if a.self_test:
        selftest()
        return
    if a.exe is None or a.output is None:
        p.error("--exe and --output are required")
    result = inspect(a.exe)
    a.output.parent.mkdir(parents=True, exist_ok=True)
    a.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print("MYTAGSYNC_REAL_BUNDLE_MANIFEST_AUDIT=PASS "
          f"entries={result['embeddedFileCount']} "
          f"categories={dict(Counter(f['licenseReview'] for f in result['embeddedFiles']))} "
          "distribution=REVIEW_REQUIRED_NOT_AUTHORIZED")

if __name__ == "__main__":
    try:
        main()
    except (BundleError, AssertionError, OSError) as exc:
        sys.exit("MYTAGSYNC_REAL_BUNDLE_MANIFEST_AUDIT=FAIL " + str(exc))
