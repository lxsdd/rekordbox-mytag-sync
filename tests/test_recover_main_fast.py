"""Synthetic regression for the hourly GitHub Actions FAST recovery workflow.

No network, credentials, actual dispatches or production data are used.
"""
from __future__ import annotations

import contextlib
import io
import json
import os
from pathlib import Path
from unittest.mock import patch
from urllib.parse import parse_qs, unquote, urlparse

WORKFLOW = (
    Path(__file__).resolve().parents[1] /
    ".github/workflows/recover-main-fast.yml"
)
SHA = "a" * 40


def script() -> str:
    lines = WORKFLOW.read_text(encoding="utf-8").splitlines()
    start = next(i for i, line in enumerate(lines) if "python3 - <<'PY'" in line)
    end = next(i for i in range(start + 1, len(lines))
               if lines[i] == "          PY")
    source = "\n".join(line[10:] for line in lines[start + 1:end]) + "\n"
    compile(source, str(WORKFLOW), "exec")
    return source


class Response:
    def __init__(self, value):
        self.payload = (
            b"" if value is None else json.dumps(value).encode("utf-8")
        )

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False

    def read(self):
        return self.payload


def exercise(*, private=False, existing=False, changed=False):
    requests = []
    dispatches = []
    branch_reads = 0
    root = "/repos/lxsdd/synthetic"
    def transport(req, timeout=20):
        nonlocal branch_reads
        assert timeout == 20
        assert req.get_header("Authorization") == "Bearer test-only"
        parsed = urlparse(req.full_url)
        assert parsed.scheme == "https"
        assert parsed.netloc == "api.github.com"
        # Detect the real regression: GET /repos/owner/name/ returns 404.
        assert parsed.path == root or parsed.path.startswith(root + "/")
        target = unquote(parsed.path[len(root):]).lstrip("/")
        requests.append((req.get_method(), parsed.path, parsed.query))
        if not target:
            assert parsed.path == root
            return Response({"private": private, "default_branch": "main"})
        if target == "actions/workflows/fast.yml":
            return Response({"state": "active",
                             "path": ".github/workflows/fast.yml"})
        if target == "branches/main":
            branch_reads += 1
            return Response({"commit": {
                "sha": "b" * 40 if changed and branch_reads > 1 else SHA
            }})
        if target == "actions/workflows/fast.yml/runs":
            params = parse_qs(parsed.query)
            assert params.get("head_sha") == [SHA]
            return Response({"total_count": 1 if existing else 0,
                             "workflow_runs": [{
                                 "head_sha": SHA, "event": "push"
                             }] if existing else []})
        if target == "actions/workflows/fast.yml/dispatches":
            assert req.get_method() == "POST"
            assert json.loads(req.data) == {"ref": "main"}
            dispatches.append(req.full_url)
            return Response(None)
        raise AssertionError("Unexpected GitHub route: " + target)

    with patch.dict(os.environ, {
        "GH_TOKEN": "test-only", "REPO": "lxsdd/synthetic"
    }), patch("urllib.request.urlopen", transport):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            try:
                exec(compile(script(), str(WORKFLOW), "exec"), {
                    "__name__": "__synthetic_recovery__"
                })
            except SystemExit as error:
                assert error.code == 0
    return requests, dispatches


def main() -> None:
    requests, dispatches = exercise()
    assert requests[0][:2] == (
        "GET", "/repos/lxsdd/synthetic"
    )
    assert len(dispatches) == 1

    _, dispatches = exercise(existing=True)
    assert dispatches == []
    _, dispatches = exercise(private=True)
    assert dispatches == []
    _, dispatches = exercise(changed=True)
    assert dispatches == []

    print("PASS: MyTagSync GitHub root URL, dedupe, private and stale SHA")


if __name__ == "__main__":
    main()
