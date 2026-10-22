#!/usr/bin/env python3
"""Pure, fail-closed GitHub workflow *publication-surface* audit.

No license clearance, YAML compiler, runner check or permission to publish is
implied. This shared contract is intentionally additive to each product's own
release, token/permission and provenance rules.
"""
from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

UPLOAD = re.compile(r"^\s*(?:-\s*)?uses:\s*actions/upload-artifact@", re.I)
RELEASE_ACTION = re.compile(
    r"^\s*(?:-\s*)?uses:\s*(?:softprops/action-gh-release|actions/create-release)@", re.I
)
RELEASE_CLI = re.compile(r"\bgh\s+release\s+(?:create|upload)\b", re.I)
INDENT_JOB = re.compile(r"^  ([A-Za-z_][A-Za-z_0-9-]*):(?:\s*#.*)?\s*$")
FALSE_IF = re.compile(r"^if:\s*false(?:\s*#.*)?$", re.I)
STEP = re.compile(r"^-\s+(?:uses:|name:|run:)", re.I)
DYNAMIC_USES = re.compile(r"^\s*(?:-\s*)?uses:\s*.*\$\{\{", re.I)


def _level(row: str) -> int:
    return len(row) - len(row.lstrip(" "))


def _disabled(lines: list[str], position: int) -> bool:
    """Only exact literal false on the containing step or job is accepted."""
    step_start = None
    step_indent = None
    for at in range(position, -1, -1):
        row = lines[at]
        if STEP.match(row.lstrip()) and not row.lstrip().startswith("#"):
            if at == position and row.lstrip().startswith(("- uses:", "- name:", "- run:")):
                step_start, step_indent = at, _level(row)
                break
            if _level(row) < _level(lines[position]):
                step_start, step_indent = at, _level(row)
                break
    if step_start is not None:
        for row in lines[step_start + 1:]:
            if row.strip() and _level(row) <= step_indent:
                break
            if _level(row) == step_indent + 2 and FALSE_IF.fullmatch(row.strip()):
                return True

    jobs = [(i, _level(lines[i])) for i in range(position + 1)
            if INDENT_JOB.match(lines[i])]
    if jobs:
        job_i, _ = jobs[-1]
        for row in lines[job_i + 1:position + 1]:
            if row.strip() and _level(row) <= 2:
                break
            if _level(row) == 4 and FALSE_IF.fullmatch(row.strip()):
                return True
    return False


def _specified_gate(lines: list[str], label: str, kind: str) -> bool:
    if kind == "job":
        positions = [i for i, line in enumerate(lines)
                     if line == "  " + label + ":"]
        if len(positions) != 1:
            return False
        for row in lines[positions[0] + 1:]:
            if row.strip() and _level(row) <= 2:
                break
            if _level(row) == 4 and FALSE_IF.fullmatch(row.strip()):
                return True
        return False
    positions = [i for i, row in enumerate(lines)
                 if row.strip() == "- name: " + label]
    return len(positions) == 1 and _disabled(lines, positions[0])


def scan_workflows(
    workflows: dict[str, str],
    expected_uploads: dict[str, int],
    required_disabled_jobs: dict[str, list[str]] | None = None,
    required_disabled_steps: dict[str, list[str]] | None = None,
    allowed_workflows: list[str] | None = None,
) -> list[str]:
    """Audit all files (not just a known workflow) and return violations."""
    errors: list[str] = []
    observed: dict[str, int] = {}
    if not workflows:
        return ["No workflows supplied"]
    if allowed_workflows is not None and set(workflows) != set(allowed_workflows):
        errors.append("Unreviewed, missing or renamed workflow")
    for name, content in sorted(workflows.items()):
        lines = content.splitlines()
        if not content.strip():
            errors.append(name + ": empty workflow")
            continue
        for index, row in enumerate(lines):
            if not row.strip() or row.lstrip().startswith("#"):
                continue
            if "\t" in row[:_level(row) + 1]:
                errors.append(f"{name}:{index + 1}: tab indentation")
            if DYNAMIC_USES.search(row):
                errors.append(f"{name}:{index + 1}: dynamic action reference")
            if UPLOAD.search(row):
                observed[name] = observed.get(name, 0) + 1
                if not _disabled(lines, index):
                    errors.append(f"{name}:{index + 1}: enabled artifact upload")
            elif RELEASE_ACTION.search(row) or RELEASE_CLI.search(row):
                if not _disabled(lines, index):
                    errors.append(f"{name}:{index + 1}: enabled release publication")
    for name, count in expected_uploads.items():
        if observed.get(name, 0) != count:
            errors.append(
                f"{name}: artifact action count {observed.get(name, 0)} != {count}"
            )
    for name in observed:
        if name not in expected_uploads:
            errors.append(f"{name}: unexpected artifact-upload workflow")
    for filename, jobs in (required_disabled_jobs or {}).items():
        lines = workflows.get(filename, "").splitlines()
        for job in jobs:
            if not _specified_gate(lines, job, "job"):
                errors.append(f"{filename}: required disabled job missing: {job}")
    for filename, steps in (required_disabled_steps or {}).items():
        lines = workflows.get(filename, "").splitlines()
        for step in steps:
            if not _specified_gate(lines, step, "step"):
                errors.append(f"{filename}: required disabled step missing: {step}")
    return errors


def audit_repository(root: Path, policy: dict) -> list[str]:
    folder = root / ".github" / "workflows"
    files = {p.name: p.read_text(encoding="utf-8")
             for p in folder.iterdir() if p.suffix in (".yml", ".yaml")} if folder.is_dir() else {}
    if policy.get("version") != 1:
        return ["Unsupported policy version"]
    return scan_workflows(
        files,
        policy.get("expected_artifact_actions", {}),
        policy.get("required_disabled_jobs", {}),
        policy.get("required_disabled_steps", {}),
        policy.get("allowed_workflows"),
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--policy", type=Path, required=True)
    args = parser.parse_args()
    policy = json.loads(args.policy.read_text(encoding="utf-8"))
    errors = audit_repository(args.root, policy)
    if errors:
        print("FAIL PUBLICATION-SURFACE:\n" + "\n".join("- " + x for x in errors))
        return 1
    print("PASS PUBLICATION-SURFACE: all reviewed upload/release paths disabled")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
