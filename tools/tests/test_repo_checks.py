"""Tests for tools/repo-checks.py.

    python3 -m unittest discover -s tools/tests

repo-checks.py decides whether this repository's invariants hold, so it is tested like
anything else that decides. Every check has, at minimum:

  * a positive case -- a synthetic tree that satisfies the rule and produces no failure,
    which proves the check is not simply failing everything; and
  * a negative case -- a tree that breaks the rule and MUST produce a failure, which is
    the only evidence the check bites.

Synthetic trees are built in temporary directories. One test at the end runs the checks
against the real repository, because the gate calling this file is the claim that the real
repository passes.

Invisible characters are written with chr() rather than escapes, so this file itself passes
text-hygiene in every editor and tool that might round-trip it.
"""
from __future__ import annotations

import os
import contextlib
import importlib.util
import io
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]
REPO = TOOLS.parent


def _load_repo_checks():
    # Registered before exec_module, or @dataclass cannot resolve its own module.
    spec = importlib.util.spec_from_file_location("repo_checks", TOOLS / "repo-checks.py")
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["repo_checks"] = module
    spec.loader.exec_module(module)
    return module


rc = _load_repo_checks()

SHA = "3d3c42e5aac5ba805825da76410c181273ba90b1"

LIBRARY = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>{name}</PackageId>
  </PropertyGroup>
  <ItemGroup>
{refs}    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" PrivateAssets="all" />
  </ItemGroup>
</Project>
"""

TOOL = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <PackAsTool>true</PackAsTool>
  </PropertyGroup>
  <ItemGroup>
{refs}  </ItemGroup>
</Project>
"""

TEST_PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit" />
  </ItemGroup>
  <ItemGroup>
{refs}  </ItemGroup>
</Project>
"""

WORKFLOW = f"""name: ci
on:
  push:
    branches: [main]
jobs:
  build:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@{SHA} # v7.0.1
      - run: ./scripts/validate.sh full
"""

README = """# fixture

See [architecture](docs/architecture.md#projects) and `tools/repo-checks.py --only layering`.
"""

CORE_CS = """namespace RulesCorpus;

/// <summary>A digest. Deterministic: it reads no clock.</summary>
public static class Digest
{
    /// <summary>Hex of the bytes.</summary>
    public static string Hex(byte[] bytes) => System.Convert.ToHexString(bytes);
}
"""

PROJECTS: dict[str, tuple[str, list[str]]] = {
    "src/RulesCorpus": ("library", []),
    "src/RulesCorpus.Adapters.Text": ("library", ["src/RulesCorpus"]),
    "src/RulesCorpus.Adapters.Xml": ("library", ["src/RulesCorpus"]),
    "src/RulesCorpus.Cli": ("tool", ["src/RulesCorpus", "src/RulesCorpus.Adapters.Text",
                                     "src/RulesCorpus.Adapters.Xml"]),
    "tests/RulesCorpus.Tests": ("test", ["src/RulesCorpus"]),
    "tests/RulesCorpus.Adapters.Text.Tests": ("test", ["src/RulesCorpus", "src/RulesCorpus.Adapters.Text"]),
    "tests/RulesCorpus.Adapters.Xml.Tests": ("test", ["src/RulesCorpus", "src/RulesCorpus.Adapters.Xml"]),
    "tests/RulesCorpus.Cli.Tests": ("test", ["src/RulesCorpus.Cli"]),
}


def _refs(directory: str, targets: list[str]) -> str:
    depth = "../" * len(Path(directory).parts)
    return "".join(
        f'    <ProjectReference Include="{depth}{t}/{Path(t).name}.csproj" />\n' for t in targets
    )


class Fixture:
    """A synthetic repository that passes every check, for a test to then break."""

    def __init__(self, root: Path) -> None:
        self.root = root
        for directory, (kind, targets) in PROJECTS.items():
            self.write_project(directory, kind, targets)
        self.write("src/RulesCorpus/Digest.cs", CORE_CS)
        self.write("src/RulesCorpus.Adapters.Text/TextAdapter.cs",
                   "namespace RulesCorpus.Adapters.Text;\n\ninternal static class TextAdapter { }\n")
        self.write("src/RulesCorpus.Cli/Program.cs",
                   "var started = System.DateTime.UtcNow;\nreturn 0;\n")
        self.write("tests/RulesCorpus.Tests/DigestTests.cs",
                   "public class DigestTests { object clock = System.DateTime.UtcNow; }\n")
        self.write("RulesCorpus.slnx", "<Solution>\n" + "".join(
            f'  <Project Path="{d}/{Path(d).name}.csproj" />\n' for d in PROJECTS) + "</Solution>\n")
        self.write(".github/workflows/ci.yml", WORKFLOW)
        self.write("README.md", README)
        self.write("docs/architecture.md", "# Architecture\n\n## Projects\n\nBack to [README](../README.md).\n")
        self.write("tools/repo-checks.py", "# stand-in\n")
        self.write("global.json", '{"sdk": {"version": "10.0.112", "rollForward": "disable"}}\n')

    def write_project(self, directory: str, kind: str, targets: list[str]) -> None:
        name = Path(directory).name
        refs = _refs(directory, targets)
        if kind == "library":
            self.write(f"{directory}/{name}.csproj", LIBRARY.format(name=name, refs=refs))
            self.write(f"{directory}/PublicAPI.Shipped.txt", "#nullable enable\n")
            self.write(f"{directory}/PublicAPI.Unshipped.txt", "#nullable enable\n")
        elif kind == "tool":
            self.write(f"{directory}/{name}.csproj", TOOL.format(refs=refs))
        else:
            self.write(f"{directory}/{name}.csproj", TEST_PROJECT.format(refs=refs))

    def write(self, rel: str, text: str) -> Path:
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(text.encode("utf-8"))
        return path

    def write_bytes(self, rel: str, data: bytes) -> Path:
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return path

    def run(self, check: str) -> list[str]:
        result = rc.CHECKS[check](self.root)
        self.examined = result.examined
        return [str(f) for f in result.failures]


class CheckTestCase(unittest.TestCase):
    check = ""

    def setUp(self) -> None:
        tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        self.repo = Fixture(tmp / "repo")

    def failures(self) -> list[str]:
        return self.repo.run(self.check)

    def assertPasses(self) -> None:
        self.assertEqual([], self.failures())
        self.assertGreater(self.repo.examined, 0, "a passing check must have examined something")

    def assertFailsWith(self, *fragments: str) -> list[str]:
        failures = self.failures()
        self.assertTrue(failures, "expected a failure, got none")
        joined = "\n".join(failures)
        for fragment in fragments:
            self.assertIn(fragment, joined)
        return failures


# ------------------------------------------------------------------------ text-hygiene


class TextHygieneTests(CheckTestCase):
    check = "text-hygiene"

    def test_clean_tree_passes(self) -> None:
        self.assertPasses()

    def test_symlink_under_src_fails_because_the_other_checks_do_not_follow_it(self) -> None:
        self.repo.write("outside/Linked.cs", "namespace X;\n")
        os.symlink("../../outside/Linked.cs", self.repo.root / "src/RulesCorpus/Linked.cs")
        self.assertFailsWith("src/RulesCorpus/Linked.cs: symbolic link")

    def test_symlink_outside_src_is_allowed(self) -> None:
        self.repo.write("docs/target.md", "# x\n")
        os.symlink("target.md", self.repo.root / "docs/alias.md")
        self.assertPasses()

    def test_bom_fails(self) -> None:
        self.repo.write_bytes("docs/bom.md", b"\xef\xbb\xbf# x\n")
        self.assertFailsWith("docs/bom.md: UTF-8 byte-order mark")

    def test_crlf_fails(self) -> None:
        self.repo.write_bytes("docs/crlf.md", b"# x\r\ny\r\n")
        self.assertFailsWith("docs/crlf.md: CRLF")

    def test_lone_cr_fails(self) -> None:
        self.repo.write_bytes("docs/cr.md", b"# x\ry\n")
        self.assertFailsWith("docs/cr.md: lone CR")

    def test_missing_trailing_newline_fails(self) -> None:
        self.repo.write_bytes("docs/eof.md", b"# x")
        self.assertFailsWith("docs/eof.md: missing trailing newline")

    def test_extra_trailing_newline_fails(self) -> None:
        self.repo.write_bytes("docs/eof.md", b"# x\n\n")
        self.assertFailsWith("docs/eof.md: more than one trailing newline")

    def test_invalid_utf8_fails(self) -> None:
        self.repo.write_bytes("docs/latin1.md", b"caf\xe9\n")
        self.assertFailsWith("docs/latin1.md: not valid UTF-8")

    def test_bidi_control_fails(self) -> None:
        self.repo.write("src/RulesCorpus/Evil.cs", f"// access {chr(0x202E)}level\n")
        self.assertFailsWith("src/RulesCorpus/Evil.cs:1:11", "U+202E")

    def test_zero_width_fails(self) -> None:
        self.repo.write("docs/zw.md", f"a{chr(0x200B)}b\n")
        self.assertFailsWith("docs/zw.md:1:2", "U+200B")

    def test_non_ascii_identifier_fails_but_non_ascii_comment_and_string_pass(self) -> None:
        self.repo.write("src/RulesCorpus/Prose.cs",
                        "// section \u00a7 and an em dash \u2014\nvar s = \"\u00a7 1\";\n")
        self.assertPasses()
        self.repo.write("src/RulesCorpus/Homoglyph.cs", f"var r{chr(0x0435)}solve = 1;\n")
        self.assertFailsWith("src/RulesCorpus/Homoglyph.cs:1:6", "U+0435")

    def test_binary_files_are_not_examined(self) -> None:
        self.repo.write_bytes("docs/plan.docx", b"PK\x03\x04\xff\xfe\r\n\x00garbage")
        self.assertPasses()

    def test_lock_file_may_lack_trailing_newline_but_not_crlf(self) -> None:
        self.repo.write_bytes("src/RulesCorpus/packages.lock.json", b"{}")
        self.assertPasses()
        self.repo.write_bytes("src/RulesCorpus/packages.lock.json", b"{\r\n}")
        self.assertFailsWith("packages.lock.json: CRLF")

    def test_defect_fixtures_under_tests_are_exempt_and_nowhere_else(self) -> None:
        self.repo.write_bytes("tests/RulesCorpus.Tests/Fixtures/invalid-bom/a.txt", b"\xef\xbb\xbfx\r\n")
        self.assertPasses()
        self.repo.write_bytes("docs/invalid/a.txt", b"\xef\xbb\xbfx\n")
        self.assertFailsWith("docs/invalid/a.txt: UTF-8 byte-order mark")

    def test_build_output_is_not_examined(self) -> None:
        self.repo.write_bytes("src/RulesCorpus/obj/generated.cs", b"x\r\n")
        self.assertPasses()


# --------------------------------------------------------------------------- parseable


class ParseableTests(CheckTestCase):
    check = "parseable"

    def test_clean_tree_passes(self) -> None:
        self.assertPasses()

    def test_malformed_xml_fails(self) -> None:
        # The real-world shape: a double hyphen inside an XML comment.
        self.repo.write("Directory.Build.props", "<Project>\n  <!-- use --force-evaluate -->\n</Project>\n")
        self.assertFailsWith("Directory.Build.props: not well-formed XML")

    def test_malformed_yaml_fails(self) -> None:
        self.repo.write(".github/workflows/broken.yml", "on: [push\njobs: {}\n")
        self.assertFailsWith(".github/workflows/broken.yml: not well-formed YAML")

    def test_malformed_json_fails(self) -> None:
        self.repo.write("global.json", '{"sdk": }\n')
        self.assertFailsWith("global.json: not well-formed JSON")

    def test_invalid_fixture_directory_under_tests_is_exempt(self) -> None:
        self.repo.write("tests/RulesCorpus.Tests/Fixtures/invalid/trailing-comma.json", '{"a": 1,}\n')
        self.repo.write("tests/RulesCorpus.Tests/Fixtures/invalid-manifests/dup.json", "{\n")
        self.assertPasses()

    def test_invalid_named_file_or_directory_outside_tests_is_not_exempt(self) -> None:
        self.repo.write("tests/RulesCorpus.Tests/Fixtures/invalid.json", "{\n")
        self.repo.write("docs/invalid/x.json", "{\n")
        failures = self.assertFailsWith("Fixtures/invalid.json", "docs/invalid/x.json")
        self.assertEqual(2, len(failures))


# ---------------------------------------------------------------------------- layering


class LayeringTests(CheckTestCase):
    check = "layering"

    def test_clean_tree_passes(self) -> None:
        self.assertPasses()

    def test_undeclared_project_fails(self) -> None:
        self.repo.write_project("src/RulesCorpus.Adapters.Html", "library", ["src/RulesCorpus"])
        self.assertFailsWith("src/RulesCorpus.Adapters.Html/RulesCorpus.Adapters.Html.csproj",
                             "not declared in ALLOWED_PROJECT_REFS")

    def test_declared_project_whose_csproj_was_deleted_fails_though_its_directory_remains(self) -> None:
        self.repo.write("src/RulesCorpus.Adapters.Text/Leftover.cs", "namespace X;\n")
        (self.repo.root / "src/RulesCorpus.Adapters.Text/RulesCorpus.Adapters.Text.csproj").unlink()
        self.assertFailsWith("src/RulesCorpus.Adapters.Text: declared in ALLOWED_PROJECT_REFS but has no csproj")

    def test_core_referencing_upward_fails(self) -> None:
        self.repo.write_project("src/RulesCorpus", "library", ["src/RulesCorpus.Adapters.Text"])
        self.assertFailsWith("src/RulesCorpus/RulesCorpus.csproj",
                             "references 'src/RulesCorpus.Adapters.Text'")

    def test_adapter_referencing_cli_fails(self) -> None:
        self.repo.write_project("src/RulesCorpus.Adapters.Text", "library",
                                ["src/RulesCorpus", "src/RulesCorpus.Cli"])
        self.assertFailsWith("references 'src/RulesCorpus.Cli'")

    def test_core_tests_referencing_the_adapter_fails(self) -> None:
        self.repo.write_project("tests/RulesCorpus.Tests", "test",
                                ["src/RulesCorpus", "src/RulesCorpus.Adapters.Text"])
        self.assertFailsWith("tests/RulesCorpus.Tests/RulesCorpus.Tests.csproj")

    def test_cli_tests_may_reference_all_three(self) -> None:
        self.repo.write_project("tests/RulesCorpus.Cli.Tests", "test",
                                ["src/RulesCorpus.Cli", "src/RulesCorpus", "src/RulesCorpus.Adapters.Text"])
        self.assertPasses()

    def test_cli_tests_may_not_reference_another_test_project(self) -> None:
        self.repo.write_project("tests/RulesCorpus.Cli.Tests", "test",
                                ["src/RulesCorpus.Cli", "tests/RulesCorpus.Tests"])
        self.assertFailsWith("references 'tests/RulesCorpus.Tests'")

    def test_single_quoted_reference_is_still_read(self) -> None:
        path = self.repo.root / "src/RulesCorpus/RulesCorpus.csproj"
        path.write_text(path.read_text().replace(
            "<ItemGroup>",
            "<ItemGroup>\n    <ProjectReference Include='../RulesCorpus.Cli/RulesCorpus.Cli.csproj' />", 1))
        self.assertFailsWith("references 'src/RulesCorpus.Cli'")

    def test_core_package_reference_fails(self) -> None:
        path = self.repo.root / "src/RulesCorpus/RulesCorpus.csproj"
        path.write_text(path.read_text().replace(
            "<ItemGroup>", '<ItemGroup>\n    <PackageReference Include="Newtonsoft.Json" />', 1))
        self.assertFailsWith("PackageReference 'Newtonsoft.Json'; the core is BCL only")

    def test_adapter_package_reference_is_not_the_core_rule(self) -> None:
        path = self.repo.root / "src/RulesCorpus.Adapters.Text/RulesCorpus.Adapters.Text.csproj"
        path.write_text(path.read_text().replace(
            "<ItemGroup>", '<ItemGroup>\n    <PackageReference Include="System.IO.Hashing" />', 1))
        self.assertPasses()

    def test_reference_injected_by_directory_build_props_fails(self) -> None:
        self.repo.write("Directory.Build.props",
                        '<Project>\n  <ItemGroup>\n    <PackageReference Include="Anything" />\n'
                        "  </ItemGroup>\n</Project>\n")
        self.assertFailsWith("Directory.Build.props: declares a PackageReference")

    def test_misnamed_csproj_in_declared_directory_fails(self) -> None:
        (self.repo.root / "src/RulesCorpus/RulesCorpus.csproj").rename(
            self.repo.root / "src/RulesCorpus/Core.csproj")
        self.assertFailsWith("src/RulesCorpus/Core.csproj", "must contain RulesCorpus.csproj")


# ----------------------------------------------------------------- solution-membership


class SolutionMembershipTests(CheckTestCase):
    check = "solution-membership"

    def test_clean_tree_passes(self) -> None:
        self.assertPasses()

    def test_project_missing_from_solution_fails(self) -> None:
        slnx = self.repo.root / "RulesCorpus.slnx"
        slnx.write_text("\n".join(
            line for line in slnx.read_text().split("\n") if "RulesCorpus.Cli.Tests" not in line))
        self.assertFailsWith("tests/RulesCorpus.Cli.Tests/RulesCorpus.Cli.Tests.csproj: on disk but not in")

    def test_solution_entry_not_on_disk_fails(self) -> None:
        slnx = self.repo.root / "RulesCorpus.slnx"
        slnx.write_text(slnx.read_text().replace(
            "</Solution>", '  <Project Path="src/Gone/Gone.csproj" />\n</Solution>'))
        self.assertFailsWith("lists 'src/Gone/Gone.csproj', which is not on disk")

    def test_missing_solution_fails(self) -> None:
        (self.repo.root / "RulesCorpus.slnx").unlink()
        self.assertFailsWith("RulesCorpus.slnx: does not exist")


# -------------------------------------------------------------------------- neutrality


class NeutralityTests(CheckTestCase):
    check = "neutrality"

    def test_clean_tree_passes(self) -> None:
        self.assertPasses()

    def test_words_that_merely_contain_a_denied_word_pass(self) -> None:
        self.repo.write("src/RulesCorpus/Words.cs",
                        "// compact, discrete, specification, values, quasar, partition, standard\n"
                        "public sealed class ValueSpan { }\n")
        self.assertPasses()

    def test_word_in_a_comment_fails_with_file_line_word(self) -> None:
        self.repo.write("src/RulesCorpus/Notes.cs", "namespace X;\n// resolves a Combat round\n")
        self.assertEqual(["src/RulesCorpus/Notes.cs:2:combat"], self.failures())

    def test_camel_case_identifier_part_fails(self) -> None:
        self.repo.write("src/RulesCorpus.Adapters.Text/Load.cs",
                        "internal sealed class Part107Loader { string SrdPage; }\n")
        self.assertFailsWith("Load.cs:1:part107", "Load.cs:1:srd")

    def test_acronym_run_is_split(self) -> None:
        self.repo.write("src/RulesCorpus/Faa.cs", "class UASWaiverStore { }\n")
        self.assertFailsWith("Faa.cs:1:uas", "Faa.cs:1:waiver")

    def test_csproj_is_scanned(self) -> None:
        path = self.repo.root / "src/RulesCorpus/RulesCorpus.csproj"
        path.write_text(path.read_text().replace(
            "<PackageId>", "<Description>Parses a regulation.</Description>\n    <PackageId>"))
        self.assertFailsWith("src/RulesCorpus/RulesCorpus.csproj:3:regulation")

    def test_tests_and_docs_are_out_of_scope(self) -> None:
        self.repo.write("tests/RulesCorpus.Tests/Srd.cs", "// the SRD spells fixture\n")
        self.repo.write("docs/consumers.md", "The FAA Part107 regulation and SRD spells.\n")
        self.assertPasses()


# ------------------------------------------------------------------------- determinism


class DeterminismTests(CheckTestCase):
    check = "determinism"

    BANNED_IN_CORE = [
        "var t = DateTime.Now;",
        "var t = DateTime.UtcNow;",
        "var t = DateTime . Today;",
        "var t = DateTimeOffset.UtcNow;",
        "var t = TimeProvider.System;",
        "var s = Stopwatch.StartNew();",
        "var r = new Random(42);",
        "var r = Random.Shared.Next();",
        "var g = Guid.NewGuid();",
        "var n = Environment.NewLine;",
        "var v = Environment.GetEnvironmentVariable(\"X\");",
        "using System.Net.Http;",
        "var c = new HttpClient();",
        "var c = new WebClient();",
        "Socket s = null;",
        "Process.Start(\"x\");",
        "var p = new ProcessStartInfo();",
        "using static System.DateTime;",
    ]

    def test_clean_tree_passes(self) -> None:
        self.assertPasses()

    def test_each_banned_api_fails_in_the_core_and_the_adapter(self) -> None:
        for scope in ("src/RulesCorpus", "src/RulesCorpus.Adapters.Text", "src/RulesCorpus.Adapters.Xml"):
            for line in self.BANNED_IN_CORE:
                with self.subTest(scope=scope, line=line):
                    path = self.repo.write(f"{scope}/Bad.cs", f"namespace X;\n{line}\n")
                    self.assertFailsWith(f"{scope}/Bad.cs:2:")
                    path.unlink()

    def test_banned_names_in_comments_and_strings_pass(self) -> None:
        self.repo.write("src/RulesCorpus/Docs.cs", "\n".join([
            "/// <summary>Never calls DateTime.UtcNow or Environment.NewLine.</summary>",
            "// new Random() and HttpClient are banned",
            "/* Process.Start, Guid.NewGuid() */",
            'var a = "DateTime.Now and System.Net";',
            'var b = @"Environment.NewLine ""quoted"" Stopwatch";',
            'var c = """',
            "    new HttpClient() in a raw literal",
            '    """;',
            "var d = 'x';",
            "",
        ]))
        self.assertPasses()

    def test_interpolation_hole_is_code(self) -> None:
        self.repo.write("src/RulesCorpus/Hole.cs", 'var s = $"at {DateTime.UtcNow:O}";\n')
        self.assertFailsWith("src/RulesCorpus/Hole.cs:1:", "DateTime clock")

    def test_cli_may_read_clock_and_environment_but_not_network(self) -> None:
        self.repo.write("src/RulesCorpus.Cli/Env.cs",
                        "var n = Environment.NewLine;\nvar t = DateTime.UtcNow;\n")
        self.assertPasses()
        self.repo.write("src/RulesCorpus.Cli/Net.cs", "var c = new HttpClient();\n")
        self.assertFailsWith("src/RulesCorpus.Cli/Net.cs:1:", "network")

    def test_tests_are_out_of_scope(self) -> None:
        self.repo.write("tests/RulesCorpus.Tests/Clock.cs", "var t = DateTime.UtcNow;\n")
        self.assertPasses()

    def test_names_that_merely_resemble_banned_ones_pass(self) -> None:
        self.repo.write("src/RulesCorpus/Near.cs",
                        "using System.Numerics;\nvar e = EnvironmentName;\nvoid ProcessSegment() { }\n")
        self.assertPasses()


# ------------------------------------------------------------------------- action-pins


class ActionPinTests(CheckTestCase):
    check = "action-pins"

    def test_clean_tree_passes(self) -> None:
        self.assertPasses()

    def test_tag_fails(self) -> None:
        self.repo.write(".github/workflows/ci.yml", WORKFLOW.replace(f"@{SHA}", "@v7"))
        self.assertFailsWith(".github/workflows/ci.yml:9: 'actions/checkout@v7'")

    def test_short_sha_fails(self) -> None:
        self.repo.write(".github/workflows/ci.yml", WORKFLOW.replace(SHA, SHA[:12]))
        self.assertFailsWith("not pinned to a 40-hex commit SHA")

    def test_quoted_uses_is_read(self) -> None:
        self.repo.write(".github/workflows/q.yml", "jobs:\n  a:\n    steps:\n      - uses: 'actions/x@main'\n")
        self.assertFailsWith("q.yml:4: 'actions/x@main'")

    def test_local_action_is_exempt(self) -> None:
        self.repo.write(".github/workflows/local.yml",
                        "jobs:\n  a:\n    steps:\n      - uses: ./.github/actions/setup\n")
        self.assertPasses()


# ---------------------------------------------------------------------- doc-references


class DocReferenceTests(CheckTestCase):
    check = "doc-references"

    def test_clean_tree_passes(self) -> None:
        self.assertPasses()

    def test_broken_relative_link_fails(self) -> None:
        self.repo.write("docs/a.md", "See [format](corpus-format.md).\n")
        self.assertFailsWith("docs/a.md:1: link 'corpus-format.md' does not resolve")

    def test_link_resolves_relative_to_the_document_not_the_root(self) -> None:
        self.repo.write("docs/a.md", "See [readme](README.md).\n")
        self.assertFailsWith("docs/a.md:1: link 'README.md'")
        self.repo.write("docs/a.md", "See [readme](../README.md) and [dir](../src/).\n")
        self.assertPasses()

    def test_anchor_and_query_are_stripped_and_urls_ignored(self) -> None:
        self.repo.write("docs/a.md", "\n".join([
            "[x](architecture.md#projects) [y](#local) [z](https://example.com/docs/nope.md)",
            "[m](mailto:someone@example.com) [q](architecture.md?plain=1) [s](<architecture.md>)",
            "",
        ]))
        self.assertPasses()

    def test_percent_encoded_link_is_decoded(self) -> None:
        self.repo.write("docs/with space.md", "# x\n")
        self.repo.write("docs/a.md", "[x](with%20space.md)\n")
        self.assertPasses()

    def test_link_escaping_the_repository_fails(self) -> None:
        self.repo.write("docs/a.md", "[x](../../outside.md)\n")
        self.assertFailsWith("escapes the repository")

    def test_reference_style_link_is_checked(self) -> None:
        self.repo.write("docs/a.md", "See [the plan][plan].\n\n[plan]: plan.docx\n")
        self.assertFailsWith("docs/a.md:3: link 'plan.docx'")

    def test_backticked_repository_path_is_checked(self) -> None:
        self.repo.write("docs/a.md", "Run `tools/missing.py --json` and read `docs/gone.md`.\n")
        self.assertFailsWith("docs/a.md:1: `tools/missing.py`", "docs/a.md:1: `docs/gone.md`")

    def test_backticked_root_file_is_checked(self) -> None:
        self.repo.write("docs/a.md", "See `global.json`, `CLAUDE.md`.\n")
        self.assertFailsWith("`CLAUDE.md` does not exist")

    def test_backticked_non_repository_tokens_are_ignored(self) -> None:
        self.repo.write("docs/a.md",
                        "`text/plain` `type/subtype` `corpus.json` `<corpus>/x` `src/**/*.cs` "
                        "`--only neutrality` `a/b`\n")
        self.assertPasses()

    def test_fenced_blocks_and_code_spans_are_not_links(self) -> None:
        self.repo.write("docs/a.md", "\n".join([
            "```",
            "[x](nowhere.md) docs/nowhere.md",
            "```",
            "Write links like `[x](nowhere.md)`.",
            "",
        ]))
        self.assertPasses()

    def test_leading_dot_slash_is_checked_from_the_root(self) -> None:
        self.repo.write("docs/a.md", "Run `./scripts/validate.sh full`.\n")
        self.assertFailsWith("`./scripts/validate.sh`")
        self.repo.write("scripts/validate.sh", "#!/bin/sh\n")
        self.assertPasses()

    def test_a_new_top_level_directory_is_a_reference_prefix(self) -> None:
        # `corpora` is not in REFERENCE_PREFIXES; it counts because it exists.
        self.assertNotIn("corpora", rc.REFERENCE_PREFIXES)
        self.repo.write("corpora/one/corpus.build.json", "{}\n")
        self.repo.write("docs/a.md", "`corpora/one/corpus.build.json` `corpora/two/corpus.build.json`\n")
        failures = self.assertFailsWith("`corpora/two/corpus.build.json`")
        self.assertEqual(1, len(failures))


# -------------------------------------------------------------------------- public-api


class PublicApiTests(CheckTestCase):
    check = "public-api"

    def test_clean_tree_passes_and_examines_the_three_libraries(self) -> None:
        self.assertPasses()
        self.assertEqual(3, self.repo.examined)

    def test_missing_unshipped_baseline_fails(self) -> None:
        (self.repo.root / "src/RulesCorpus.Adapters.Text/PublicAPI.Unshipped.txt").unlink()
        self.assertFailsWith("RulesCorpus.Adapters.Text.csproj: packable library has no PublicAPI.Unshipped.txt")

    def test_missing_analyzer_fails(self) -> None:
        path = self.repo.root / "src/RulesCorpus/RulesCorpus.csproj"
        path.write_text("\n".join(
            line for line in path.read_text().split("\n") if "PublicApiAnalyzers" not in line))
        self.assertFailsWith("does not reference Microsoft.CodeAnalysis.PublicApiAnalyzers")

    def test_tool_and_non_packable_projects_are_exempt(self) -> None:
        self.repo.write("src/RulesCorpus.Cli/Extra.cs", "// the tool has no baselines\n")
        self.assertPasses()
        self.assertFalse((self.repo.root / "src/RulesCorpus.Cli/PublicAPI.Shipped.txt").exists())


# ----------------------------------------------------------------- command-line contract


class CommandLineTests(unittest.TestCase):
    def setUp(self) -> None:
        tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        self.tmp = tmp

    def main(self, *args: str) -> tuple[int, str]:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = rc.main(list(args))
        return code, out.getvalue()

    def test_clean_fixture_passes_every_check(self) -> None:
        repo = Fixture(self.tmp / "repo")
        code, out = self.main("--root", str(repo.root), "--json")
        report = json.loads(out)
        self.assertEqual(0, code, out)
        self.assertEqual([], report["skipped"])
        self.assertEqual(set(rc.CHECKS), set(report["checks"]))

    def test_a_failure_exits_non_zero(self) -> None:
        repo = Fixture(self.tmp / "repo")
        repo.write(".github/workflows/ci.yml", WORKFLOW.replace(f"@{SHA}", "@v7"))
        code, out = self.main("--root", str(repo.root), "--only", "action-pins")
        self.assertEqual(1, code)
        self.assertIn("FAIL  action-pins", out)

    def test_skip_fails_unless_allowed(self) -> None:
        empty = self.tmp / "empty"
        empty.mkdir()
        code, out = self.main("--root", str(empty), "--only", "action-pins", "--json")
        self.assertEqual(1, code)
        self.assertEqual(["action-pins"], json.loads(out)["skipped"])
        self.assertTrue(json.loads(out)["skip_fails_run"])
        code, _ = self.main("--root", str(empty), "--only", "action-pins", "--allow-skip")
        self.assertEqual(0, code)

    def test_only_is_repeatable_and_rejects_unknown_checks(self) -> None:
        repo = Fixture(self.tmp / "repo")
        code, out = self.main("--root", str(repo.root), "--only", "layering",
                              "--only", "public-api", "--json")
        self.assertEqual(0, code)
        self.assertEqual({"layering", "public-api"}, set(json.loads(out)["checks"]))
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            self.main("--only", "no-such-check")

    def test_git_checkout_includes_untracked_files_and_excludes_ignored_ones(self) -> None:
        repo = Fixture(self.tmp / "repo")
        subprocess.run(["git", "init", "-q"], cwd=repo.root, check=True)
        repo.write(".gitignore", "scratch/\n")
        subprocess.run(["git", "add", "README.md"], cwd=repo.root, check=True)
        repo.write_bytes("docs/untracked.md", b"x\r\n")
        repo.write_bytes("scratch/ignored.md", b"x\r\n")
        failures = repo.run("text-hygiene")
        self.assertEqual(["docs/untracked.md: CRLF line endings (LF required)"], failures)


# ---------------------------------------------------------------- the real repository


class RealRepositoryTests(unittest.TestCase):
    def test_the_repository_passes_every_check_with_no_skips(self) -> None:
        done = subprocess.run(
            [sys.executable, str(TOOLS / "repo-checks.py"), "--json"],
            cwd=REPO, capture_output=True, text=True, check=False)
        report = json.loads(done.stdout)
        problems = {name: c["failures"] for name, c in report["checks"].items() if c["failures"]}
        self.assertEqual({}, problems)
        self.assertEqual([], report["skipped"])
        self.assertEqual(0, done.returncode)


if __name__ == "__main__":
    unittest.main()
