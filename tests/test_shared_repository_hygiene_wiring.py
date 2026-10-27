"""Shared FAST/FULL/owner-only repository hygiene wiring and negative tests."""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CALL = "& ./scripts/Assert-RepositoryHygiene.ps1"
PINNED = "9fc78008a50e67f8ee01110f5ee4939935edb8a9"
LEGACY = "9e35339addd5cd07241e70cf2d8afadccd9cce42"


def check(workflows, scanner):
    for name, text in workflows.items():
        if text.count(CALL) != 1:
            raise AssertionError(name + " did not invoke one shared audit")
        if text.count("- name: Audit repository credential and privacy hygiene") != 1:
            raise AssertionError(name + " lost the required hygiene gate")
        if "function Test-Content(" in text:
            raise AssertionError(name + " still has a divergent scanner")
    if "fetch-depth: 0" not in workflows["FAST"]:
        raise AssertionError("FAST lacks the historical blob inventory")
    required = [
        PINNED, LEGACY, "MYTAGSYNC_STRICT_HISTORICAL_HYGIENE_REGRESSION=PASS",
        "git rev-list --objects --all", "$script:findings",
        "Test-Content", "kind = 'wrong-sha'", "kind = 'wrong-path'",
        "kind = 'second-profile'", "kind = 'credential'",
        "Repository hygiene audit found credential-like or private local-path material",
    ]
    for marker in required:
        if marker not in scanner:
            raise AssertionError("Missing strict historical audit rule: " + marker)


def main():
    files = {
        "FAST": ROOT / ".github/workflows/fast.yml",
        "FULL": ROOT / ".github/workflows/full.yml",
        "OWNER": ROOT / ".github/workflows/owner-private-readonly.yml",
    }
    workflows = {name: file.read_text(encoding="utf-8") for name, file in files.items()}
    scanner = (ROOT / "scripts/Assert-RepositoryHygiene.ps1").read_text(encoding="utf-8")
    check(workflows, scanner)
    trials = [
        ("owner missing", {**workflows, "OWNER": workflows["OWNER"].replace(CALL, "echo NONE")}, scanner),
        ("full missing", {**workflows, "FULL": workflows["FULL"].replace(CALL, "echo NONE")}, scanner),
        ("shallow clone", {**workflows, "FAST": workflows["FAST"].replace("fetch-depth: 0", "fetch-depth: 1")}, scanner),
        ("pin removed", workflows, scanner.replace(PINNED, "0" * 40)),
        ("token regression removed", workflows, scanner.replace("kind = 'credential'", "kind = 'omitted'")),
    ]
    for description, wf, script in trials:
        try:
            check(wf, script)
        except AssertionError:
            continue
        raise AssertionError("Negative wiring test wrongly passed: " + description)
    print("MYTAGSYNC_SHARED_HYGIENE_WIRING=PASS")


if __name__ == "__main__":
    main()
