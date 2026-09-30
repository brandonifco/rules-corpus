#!/usr/bin/env python3
"""expected-test-projects -- how many test runs (project x target framework) this repository
should produce.

scripts/validate.sh compares this count against the number of TRX result files a test
pass actually produced. The expectation is derived from the repository rather than from
RulesKernel.slnx on purpose: a project dropped from the solution also drops out of a count
taken from the solution, so the expectation would fall in step with the actual and the
assertion could never fail. That was verified -- against a solution-derived expectation,
deleting a project line from the slnx still reported PASS.

It lives here rather than inline in validate.sh because the guard's own correctness is the
point, and a guard nothing tests is the thing it exists to prevent. tools/tests/ tests it.

The IGNORED comparison is against the path RELATIVE TO the root, which is the whole reason
this was extracted. Comparing absolute parts meant the location of the checkout could
silently zero the count: run from a worktree at .claude/worktrees/agent-X, every csproj
path contained a `worktrees` segment, every project was skipped, and validate.sh reported
"A test project silently stopped running" on a clean tree. A guard that fails for reasons
unrelated to the change is one people learn to work around, and the obvious workaround is
deleting the guard.
"""
from __future__ import annotations

import pathlib
import re
import sys

# Build output, tool caches, and nested checkouts. `worktrees` covers .claude/worktrees/,
# where a separate checkout of this repository lives: its projects are that branch's, not
# this tree's.
IGNORED = {
    "bin", "obj", ".git", ".dotnet", ".venv", "artifacts", "TestResults", "worktrees",
}

IS_TEST_PROJECT = re.compile(r"<IsTestProject>\s*true\s*</IsTestProject>", re.IGNORECASE)

# `dotnet test` writes one result file per target framework of a multi-targeted project, so
# the expectation counts (project, framework) pairs. A project without its own
# <TargetFrameworks> builds the one framework Directory.Build.props sets.
TARGET_FRAMEWORKS = re.compile(r"<TargetFrameworks>([^<]*)</TargetFrameworks>", re.IGNORECASE)


def frameworks(text: str) -> int:
    """How many target frameworks a project's own text declares; 1 when it declares none."""
    declared = TARGET_FRAMEWORKS.search(text)
    if declared is None:
        return 1
    return max(1, len([f for f in declared.group(1).split(";") if f.strip()]))


def count(root: pathlib.Path) -> int:
    """Test runs expected under `root`: each test project once per target framework, ignoring
    build output and nested checkouts."""
    found = 0
    for csproj in sorted(root.rglob("*.csproj")):
        if any(part in IGNORED for part in csproj.relative_to(root).parts):
            continue
        text = csproj.read_text(encoding="utf-8", errors="replace")
        if IS_TEST_PROJECT.search(text):
            found += frameworks(text)
    return found


def main(argv: list[str]) -> int:
    print(count(pathlib.Path(argv[1] if len(argv) > 1 else ".")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
