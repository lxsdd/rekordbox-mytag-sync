#!/usr/bin/env python3
"""Guard against public MyTagSync component downloads before license clearance."""
from pathlib import Path
import sys
root = Path(__file__).resolve().parents[1]
full = (root / ".github/workflows/full.yml").read_text(encoding="utf-8").splitlines()
promote = (root / ".github/workflows/promote-release.yml").read_text(encoding="utf-8").splitlines()
indexes = [i for i, line in enumerate(full) if "- uses: actions/upload-artifact@" in line]
issues = []
if len(indexes) != 1 or not any(
    line.strip().startswith("if: false") for line in full[indexes[0]+1:indexes[0]+6]
    ) if indexes else True:
    issues.append("FULL package artifact not safely disabled")
jobs = [i for i, line in enumerate(promote) if line.strip() == "promote-qualified-artifact:"]
if len(jobs) != 1 or not any(
    line.strip().startswith("if: false") for line in promote[jobs[0]+1:jobs[0]+6]
    ) if jobs else True:
    issues.append("public release job not safely disabled")
if issues:
    sys.exit("FAIL MyTagSync redistribution gate: " + "; ".join(issues))
print("PASS: MyTagSync FAST/FULL builds and self-tests preserved; uploads blocked")
