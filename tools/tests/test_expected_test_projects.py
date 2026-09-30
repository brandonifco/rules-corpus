"""The guard that counts test projects is itself a guard, so it is itself tested.

scripts/validate.sh trusts this number to decide whether every test project actually ran.
A count that is silently zero turns that assertion into a false failure on a clean tree --
which is what happened in rules-kernel from a git worktree, and a gate that fails for
reasons unrelated to the change is one people work around by deleting it.
"""
from __future__ import annotations

import importlib.util
import pathlib
import shutil
import tempfile
import unittest

_spec = importlib.util.spec_from_file_location(
    "expected_test_projects",
    pathlib.Path(__file__).resolve().parents[1] / "expected-test-projects.py")
assert _spec is not None and _spec.loader is not None
etp = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(etp)

TEST_PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
</Project>
"""
LIBRARY_PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><PackageId>Thing</PackageId></PropertyGroup>
</Project>
"""


class ExpectedTestProjectsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)

    def tree(self, root: pathlib.Path) -> pathlib.Path:
        """A miniature repository: two test projects and one library."""
        self.write(root / "tests/A/A.csproj", TEST_PROJECT)
        self.write(root / "tests/B/B.csproj", TEST_PROJECT)
        self.write(root / "src/Lib/Lib.csproj", LIBRARY_PROJECT)
        return root

    @staticmethod
    def write(path: pathlib.Path, text: str) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def test_it_counts_test_projects_and_not_libraries(self) -> None:
        self.assertEqual(2, etp.count(self.tree(self.tmp / "repo")))

    def test_a_checkout_whose_own_path_is_ignored_still_counts(self) -> None:
        # The bug: from a worktree at .claude/worktrees/agent-X every csproj path contained
        # a `worktrees` segment, so every project was skipped and the expectation became 0.
        root = self.tree(self.tmp / ".claude" / "worktrees" / "agent-x")

        self.assertEqual(2, etp.count(root))

    def test_every_ignored_name_is_harmless_in_the_root_path(self) -> None:
        for name in sorted(etp.IGNORED):
            with self.subTest(directory=name):
                root = self.tree(self.tmp / name / f"repo-{name.strip('.')}")
                self.assertEqual(2, etp.count(root))

    def test_a_nested_checkout_inside_the_tree_is_still_skipped(self) -> None:
        # A worktree WITHIN the repository is a different branch's checkout.
        root = self.tree(self.tmp / "repo")
        self.write(root / ".claude/worktrees/agent-y/tests/C/C.csproj", TEST_PROJECT)

        self.assertEqual(2, etp.count(root))

    def test_build_output_is_still_skipped(self) -> None:
        root = self.tree(self.tmp / "repo")
        self.write(root / "tests/A/obj/Debug/A.csproj", TEST_PROJECT)
        self.write(root / "tests/A/bin/Debug/A.csproj", TEST_PROJECT)

        self.assertEqual(2, etp.count(root))

    def test_a_tree_with_no_test_projects_counts_zero(self) -> None:
        # validate.sh's second assertion (tests actually ran) catches this; the count must
        # simply report it honestly.
        root = self.tmp / "empty"
        self.write(root / "src/Lib/Lib.csproj", LIBRARY_PROJECT)

        self.assertEqual(0, etp.count(root))


if __name__ == "__main__":
    unittest.main()
