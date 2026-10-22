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

# Use the common, SHA-pinned synthetically qualified publication scanner.
# All original MyTagSync-specific workflow/token/owner-private checks BELOW
# remain in effect, along with disabled FULL and release checks ABOVE.
from _shared_publication_guard import scan_workflows as _scan_publication_surface

_D_EXPECTED = {"full.yml": 1}
_D_DIR = Path(__file__).resolve().parents[1] / ".github/workflows"
_D_FILES = {p.name: p.read_text(encoding="utf-8") for p in _D_DIR.iterdir()
            if p.suffix.lower() in (".yml", ".yaml")}
_D_ERRORS = _scan_publication_surface(
    _D_FILES, _D_EXPECTED,
    required_disabled_jobs={"promote-release.yml": ["promote-qualified-artifact"]},
    allowed_workflows=[
        "fast.yml", "full.yml", "promote-release.yml", "recover-main-fast.yml",
        "owner-private-readonly.yml"],
)
if _D_ERRORS:
    sys.exit("FAIL shared publication audit:\n- " + "\n- ".join(_D_ERRORS))
_D_MUTATION = dict(_D_FILES)
_D_MUTATION["new-upload.yml"] = (
    "jobs:\n  leak:\n    runs-on: ubuntu-latest\n    steps:\n"
    "      - uses: actions/upload-artifact@v7\n"
)
if not _scan_publication_surface(_D_MUTATION, _D_EXPECTED):
    sys.exit("FAIL: unreviewed upload escaped shared audit")
print("PASS shared publication audit; MyTag-specific privileges and Draft gates retained")

# Third boundary: a new workflow, job-level token override, or privilege increase
# must never silently make GitHub's public-repo publication safeguards irrelevant.
# Deliberately narrow: not a generic YAML validator or a license approval.
_ALLOWED_WORKFLOWS = {
    "fast.yml": (
        {"contents": "read"},
        {"push", "pull_request", "workflow_dispatch"}),
    "full.yml": (
        {"contents": "read", "actions": "read"},
        {"workflow_dispatch", "workflow_run"}),
    "recover-main-fast.yml": (
        {"contents": "read", "actions": "write"},
        {"schedule", "workflow_dispatch"}),
    "promote-release.yml": (
        {"contents": "write", "actions": "read"},
        {"workflow_dispatch"}),
    # Exceptional, single-owner manual transfer. No push/PR/workflow_run
    # trigger and no Actions artifacts: upload ONLY through confirmed Draft.
    "owner-private-readonly.yml": (
        {"contents": "write", "actions": "read"},
        {"workflow_dispatch"}),
}


def _audit_workflow_capabilities(_files):
    import re as _re

    errors = []
    if set(_files) != set(_ALLOWED_WORKFLOWS):
        errors.append("unreviewed, missing, or renamed GitHub workflow")
    for name, (expected_permissions, expected_events) in _ALLOWED_WORKFLOWS.items():
        if name not in _files:
            continue
        lines = _files[name].splitlines()
        permission_headers = [
            i for i, s in enumerate(lines)
            if _re.match(r"^permissions\s*:\s*(?:#.*)?$", s)]
        if len(permission_headers) != 1:
            errors.append(name + ": missing/duplicate explicit top-level permissions")
            continue
        start = permission_headers[0]
        permissions = {}
        for line in lines[start + 1:]:
            if line.strip() == "" or line.lstrip().startswith("#"):
                continue
            if not line[0].isspace():
                break
            match = _re.fullmatch(r"  ([a-z][a-z-]*): (read|write|none)\s*", line)
            if not match or match.group(1) in permissions:
                errors.append(name + ": ambiguous/duplicate permission")
                break
            permissions[match.group(1)] = match.group(2)
        if permissions != expected_permissions:
            errors.append(name + ": token capabilities exceed the reviewed baseline")
        if any(_re.match(r"^\s+permissions\s*:", s) for s in lines):
            errors.append(name + ": job-level permissions override is prohibited")

        event_headers = [
            i for i, s in enumerate(lines)
            if _re.match(r"^on:\s*(?:#.*)?$", s)]
        if len(event_headers) != 1:
            errors.append(name + ": ambiguous workflow triggers")
            continue
        events = []
        for line in lines[event_headers[0] + 1:]:
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            if not line[0].isspace():
                break
            match = _re.match(r"^  ([a-z_]+):", line)
            if match:
                events.append(match.group(1))
        if set(events) != expected_events or len(events) != len(expected_events):
            errors.append(name + ": unreviewed or missing trigger")
    return errors


_PRIV_ERRORS = _audit_workflow_capabilities(_D_FILES)
if _PRIV_ERRORS:
    sys.exit("FAIL MyTagSync workflow-token boundary:\n- " +
             "\n- ".join(_PRIV_ERRORS))



# A manually dispatched write token is only tolerated for this very specific
# SHA-bound, owner-only, draft-only transfer. Fail closed on changes to the
# authenticated identity, source, legal confirmation, or draft upload path.
_OWNER_NAME = "owner-private-readonly.yml"
_OWNER_TEXT = _D_FILES[_OWNER_NAME]
_OWNER_REQUIRED = (
    "github.actor == 'lxsdd'",
    "github.repository == 'lxsdd/rekordbox-mytag-sync'",
    "github.ref == 'refs/heads/main'",
    "Verify owner authorization and previous SHA-exact green FAST/FULL",
    "PRIVATE_HANDOFF_QUALIFICATION=PASS",
    "scripts/Upload-OwnerPrivateDraft.ps1",
    "FULL-QUALIFICATION.json",
    "persist-credentials: false",
    "EXPECTED_ZIP:",
    "OWNER_ACK:",
)
_OWNER_SCRIPT_PATH = Path(__file__).resolve().parents[1] / "scripts/Upload-OwnerPrivateDraft.ps1"
_OWNER_SCRIPT = _OWNER_SCRIPT_PATH.read_text(encoding="utf-8")
_OWNER_SCRIPT_REQUIRED = (
    "Assert-Inputs $SourceSha $ExpectedZipSha256 $Acknowledgement",
    "I_ACCEPT_PRIVATE_MICROSOFT_DOTNET_AND_SDK_DISTRIBUTION_DUTIES",
    "Get-FileHash -LiteralPath $zip -Algorithm SHA256",
    "FULL-QUALIFICATION.json",
    "if ($main.commit.sha -cne $SourceSha)",
    "draft=$true",
    "Invoke-RestMethod -Method Post -Uri \"$api/releases\"",
    "if ($release.draft -ne $true",
    "if ($final.draft -ne $true",
    "if ([int]$response.StatusCode -ne 404)",
    "PRIVATE_DRAFT_PREUPLOAD_ANONYMOUS_DENY=PASS",
    "Assert-Anonymous404 ([string]$asset.browser_download_url)",
)
for required in _OWNER_REQUIRED:
    if required not in _OWNER_TEXT:
        sys.exit("FAIL private draft owner/SHA boundary missing: " + required)
for required in _OWNER_SCRIPT_REQUIRED:
    if required not in _OWNER_SCRIPT:
        sys.exit("FAIL private draft endpoint/sha/privacy/terms boundary missing: " + required)
# The new draft's anonymous denial MUST precede the very first binary
# upload. Checking only after upload could already expose a real executable.
_preupload = _OWNER_SCRIPT.find("Assert-Anonymous404 ([string]$release.html_url)")
_transfer = _OWNER_SCRIPT.find("Invoke-RestMethod -Method Post -Uri $upload")
if not (0 <= _preupload < _transfer):
    sys.exit("FAIL: fresh Draft anonymity was not checked BEFORE binary upload")
if _OWNER_SCRIPT.find("Assert-Anonymous404 ([string]$asset.browser_download_url)") < _transfer:
    sys.exit("FAIL: individual uploaded asset privacy check missing after upload")
_mutated_script = _OWNER_SCRIPT.replace(
    "Assert-Anonymous404 ([string]$release.html_url)", "# omitted before upload", 1)
if "Assert-Anonymous404 ([string]$release.html_url)" in _mutated_script:
    sys.exit("FAIL: owner pre-upload negative regression did not remove its guard")

if ("actions/upload-artifact@" in _OWNER_TEXT or
    "gh release create" in _OWNER_TEXT or
    "gh release edit" in _OWNER_TEXT or
    "--draft=false" in _OWNER_TEXT or
    "draft=$false" in _OWNER_SCRIPT):
    sys.exit("FAIL private draft workflow contains public/Actions publication escape")
for required in _OWNER_REQUIRED:
    if required not in _OWNER_TEXT.replace(required, "", 1):
        continue
# This exact filename may not silently become a public workflow.
_synthetic_owner = _OWNER_TEXT.replace("github.actor == 'lxsdd'",
                                       "github.actor != 'lxsdd'", 1)
if all(r in _synthetic_owner for r in _OWNER_REQUIRED):
    sys.exit("FAIL synthetic owner-auth bypass escaped the draft gate")
# The owner-only Draft script is PowerShell, not a native executable.
# A stale or null $LASTEXITCODE after an otherwise successful .ps1 call is NOT
# a valid result and previously produced a misleading failed Actions run.
_owner_workflow_tail = _OWNER_TEXT.split(
    "- name: Transfer exact FULL-matched candidate ONLY into a private Draft", 1)
if len(_owner_workflow_tail) != 2:
    sys.exit("FAIL: owner-only Draft transfer step missing or ambiguous")
if "if ($LASTEXITCODE -ne 0)" in _owner_workflow_tail[1]:
    sys.exit("FAIL: owner Draft transfer checks unrelated native LASTEXITCODE")
print("PASS owner-only, terms-acknowledged, version/ZIP-qualified draft upload guard")

def _reject_bad_capabilities(changed):
    trial = dict(_D_FILES)
    trial.update(changed)
    if not _audit_workflow_capabilities(trial):
        sys.exit("FAIL: synthetic privilege/escalation bypassed workflow audit")


# Every exception is explicit and bound to a single reviewed workflow:
# recover FAST needs actions:write to dispatch, but never contents:write.
# Disabled PROMOTE is the only workflow whose contents:write is permitted.
_recover = _D_FILES["recover-main-fast.yml"]
_fast = _D_FILES["fast.yml"]
_promote = _D_FILES["promote-release.yml"]
if "  actions: write" not in _recover or "  contents: write" not in _promote:
    sys.exit("FAIL: workflow baseline changed; update reviewed negative fixtures")
_reject_bad_capabilities({"fast.yml": _fast.replace(
    "  contents: read", "  contents: write", 1)})
_reject_bad_capabilities({"recover-main-fast.yml": _recover.replace(
    "  actions: write", "  contents: write", 1)})
_reject_bad_capabilities({"promote-release.yml": _promote.replace(
    "  contents: write", "  contents: read", 1)})
_reject_bad_capabilities({"fast.yml": _fast.replace(
    "jobs:\n", "jobs:\n  unreviewed:\n    permissions:\n"
              "      contents: write\n    runs-on: ubuntu-latest\n", 1)})
_reject_bad_capabilities({"fast.yml": _fast.replace(
    "on:\n", "on:\n  pull_request_target:\n", 1)})
_reject_bad_capabilities({"unreviewed.yml": (
    "name: publish\non:\n  workflow_dispatch:\n"
    "permissions:\n  contents: write\njobs:\n  publish:\n"
    "    runs-on: ubuntu-latest\n    steps: []\n")})
print("PASS: exact GitHub workflow allowlist, token scopes, triggers and "
      "negative privilege-escalation fixtures")
