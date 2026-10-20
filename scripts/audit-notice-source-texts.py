#!/usr/bin/env python3
"""Preserve bytes of pinned license/notice *sources*, without approving delivery.

Inputs are explicit files on a disposable trusted CI runner, never user files.
The resulting review folder is NOT a complete ThirdPartyNotices distribution
and MUST NOT be presented as proof that all obligations are satisfied.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import tempfile

SAFE_ID = re.compile(r"^[A-Z][A-Z0-9_]{3,95}$")
MAX_TEXT = 5 * 1024 * 1024


class NoticeError(ValueError):
    pass


def collect(file_definitions, output_dir):
    if not file_definitions:
        raise NoticeError("no source notice files were supplied")
    output_dir = Path(output_dir)
    if output_dir.exists() and any(output_dir.iterdir()):
        raise NoticeError("refusing to reuse nonempty evidence output folder")
    prepared = []
    seen = set()
    for item in file_definitions:
        if "=" not in item:
            raise NoticeError("source notice must be SOURCE_ID=exact-file")
        name, path_text = item.split("=", 1)
        if not SAFE_ID.fullmatch(name) or name in seen:
            raise NoticeError("invalid or duplicate notice source identifier")
        seen.add(name)
        path = Path(path_text)
        if (path.is_symlink() or not path.is_file() or
                path.suffix.lower() not in (".txt", ".md", ".license")):
            raise NoticeError("notice source missing, linked or not text: " + name)
        size = path.stat().st_size
        if not 20 <= size <= MAX_TEXT:
            raise NoticeError("notice text has unsafe size: " + name)
        raw = path.read_bytes()
        if len(raw) != size or b"\x00" in raw:
            raise NoticeError("notice source changed or contains binary data: " + name)
        try:
            raw.decode("utf-8-sig", "strict")
        except UnicodeDecodeError as exc:
            raise NoticeError("notice source is not UTF-8 text: " + name) from exc
        prepared.append((name, raw))
    output_dir.mkdir(parents=True, exist_ok=True)
    entries = []
    for name, raw in sorted(prepared):
        target_name = name + ".txt"
        (output_dir / target_name).write_bytes(raw)
        entries.append(dict(sourceId=name, stagedName=target_name,
                            bytes=len(raw),
                            sha256=hashlib.sha256(raw).hexdigest(),
                            status="RAW_SOURCE_TEXT_UNREVIEWED"))
    report = dict(schemaVersion=1,
                  distributionLicenseStatus="REVIEW_REQUIRED_NOT_AUTHORIZED",
                  completeness="NOT_ESTABLISHED",
                  sourceTexts=entries)
    return report


def selftest():
    with tempfile.TemporaryDirectory(prefix="mytagsync-notice-test-") as tmp:
        root = Path(tmp)
        origin = root / "notice.txt"
        origin.write_bytes(b"FICTIONAL TEST NOTICE, NOT A REAL LICENSE.\n")
        spec = ["DOTNET_RUNTIME_NOTICE_TEST=" + str(origin)]
        output = root / "collected"
        valid = collect(spec, output)
        assert len(valid["sourceTexts"]) == 1
        assert valid["distributionLicenseStatus"] == "REVIEW_REQUIRED_NOT_AUTHORIZED"
        assert valid["completeness"] == "NOT_ESTABLISHED"
        assert valid["sourceTexts"][0]["sha256"] == hashlib.sha256(origin.read_bytes()).hexdigest()
        assert (output / "DOTNET_RUNTIME_NOTICE_TEST.txt").read_bytes() == origin.read_bytes()
        assert str(root.resolve()) not in json.dumps(valid)
        tests = [
            (spec, output),  # refuse overwrites
            (spec + spec, root / "duplicated"),
            (["invalid-id=" + str(origin)], root / "invalid"),
            (["MISSING_NOTICE_FILE=/nonexistent/notice.txt"], root / "missing"),
        ]
        for entries, destination in tests:
            try:
                collect(entries, destination)
            except NoticeError:
                pass
            else:
                raise AssertionError("an unsafe notice collection passed")
        origin.write_bytes(b"REAL\x00BINARY THAT MUST NOT BE INCLUDED")
        try:
            collect(spec, root / "binary")
        except NoticeError:
            pass
        else:
            raise AssertionError("binary source accepted as legal notice")
    print("MYTAGSYNC_NOTICE_SOURCE_PRESERVATION_SELFTEST=PASS")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--source-file", action="append", default=[])
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    if args.self_test:
        selftest()
        return
    if args.output_dir is None or args.report is None:
        parser.error("--output-dir and --report are required")
    report = collect(args.source_file, args.output_dir)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("MYTAGSYNC_NOTICE_SOURCE_PRESERVATION=PASS "
          f"files={len(report['sourceTexts'])} "
          "completeness=NOT_ESTABLISHED distribution=REVIEW_REQUIRED_NOT_AUTHORIZED")


if __name__ == "__main__":
    try:
        main()
    except (NoticeError, OSError, AssertionError) as exc:
        sys.exit("MYTAGSYNC_NOTICE_SOURCE_PRESERVATION=FAIL " + str(exc))
