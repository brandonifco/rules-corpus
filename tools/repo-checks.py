#!/usr/bin/env python3
"""repo-checks -- mechanical enforcement of this repository's invariants.

A rule stated only in prose does not fail a build. These do (CLAUDE.md principle 4).

    tools/repo-checks.py                  run every check
    tools/repo-checks.py --json           machine-readable result
    tools/repo-checks.py --only layering  one check (repeatable)
    tools/repo-checks.py --root <dir>     run against another tree (the tests do this)

Checks:
  text-hygiene        UTF-8, no BOM, LF endings, one trailing newline, no invisible or
                      direction-changing characters, ASCII-only C# code
  parseable           every XML, YAML and JSON file parses
  layering            every csproj's ProjectReferences are within ALLOWED_PROJECT_REFS, and
                      src/RulesCorpus has no PackageReference but the public-API analyzer
  solution-membership every csproj on disk is in RulesCorpus.slnx, and vice versa
  neutrality          no known consumer's domain vocabulary in src/ (.cs, .csproj)
  determinism         no clock, randomness, environment, process or network in the core
                      and the text adapter; no network in the CLI
  action-pins         every GitHub Action is pinned to a 40-hex commit SHA
  doc-references      every relative markdown link and backticked repository path in a
                      .md file resolves -- a citation is a promise
  public-api          every packable library in src/ has PublicAPI baselines and the
                      analyzer that enforces them

A check that examined nothing reports `skip`, and a skip fails the run: every check here has
inputs in this repository, so examining nothing means the check lost its inputs, not that
the repository is clean. `--allow-skip` exists for synthetic trees in tools/tests/ only and
must never be used by the gate.

Defect fixtures. A directory under tests/ whose name starts with `invalid` (for example
tests/RulesCorpus.Tests/Fixtures/invalid-json/) holds deliberately malformed input. Files
beneath one are exempt from text-hygiene and parseable, because their defects are the point.
Nothing else is exempt, and nothing outside tests/ can use the convention.

What this file does NOT prove
-----------------------------
neutrality and determinism are deny lists over source text. A deny list catches accidents
and people who do not know the rule. It does not catch intent: a word the list does not
name, vocabulary assembled from fragments, a banned API reached through reflection, a
`using` alias, a target-typed `new()`, or a helper in another assembly all pass. Review is
the other net; the tests that assert byte-identical rebuilds are the evidence that matters.
doc-references proves a citation resolves, not that it cites the right thing.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import urllib.parse
import xml.etree.ElementTree as ElementTree
import xml.parsers.expat
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

SOLUTION_FILE = "RulesCorpus.slnx"

# Build output, tool caches and nested checkouts, compared against the path RELATIVE TO the
# root: comparing absolute parts would make the checkout's own location part of the answer
# (a worktree under .claude/worktrees/ once zeroed a count that way in rules-kernel).
IGNORED_PARTS = {
    ".git", "bin", "obj", ".dotnet", ".venv", "__pycache__", "artifacts", "TestResults",
    "node_modules", ".vs", "packages", "worktrees",
}

TEXT_SUFFIXES = {
    ".cs", ".csproj", ".props", ".targets", ".slnx", ".sln", ".json", ".md", ".yml", ".yaml",
    ".sh", ".py", ".txt", ".xml", ".config", ".editorconfig", ".gitattributes", ".gitignore",
}
TEXT_NAMES = {"LICENSE", ".editorconfig", ".gitattributes", ".gitignore"}
BINARY_SUFFIXES = {
    ".docx", ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".zip", ".tar", ".gz",
    ".nupkg", ".snupkg", ".dll", ".exe", ".pdb", ".so", ".dylib", ".bin",
}

XML_SUFFIXES = {".csproj", ".props", ".targets", ".slnx", ".config", ".xml", ".nuspec", ".resx"}
YAML_SUFFIXES = {".yml", ".yaml"}

# ---------------------------------------------------------------- the declared graph
#
# Keyed by project directory relative to the root; the csproj inside is named after the
# directory. The value is the set of projects each may reference -- a subset, not a
# requirement. docs/architecture.md and docs/decisions/0001 are the prose this encodes.
# A csproj on disk that is not declared here fails: an undeclared project must not
# silently escape enforcement.
ALLOWED_PROJECT_REFS: dict[str, set[str]] = {
    "src/RulesCorpus": set(),
    "src/RulesCorpus.Adapters.Text": {"src/RulesCorpus"},
    "src/RulesCorpus.Cli": {"src/RulesCorpus", "src/RulesCorpus.Adapters.Text"},
    "tests/RulesCorpus.Tests": {"src/RulesCorpus"},
    "tests/RulesCorpus.Adapters.Text.Tests": {"src/RulesCorpus", "src/RulesCorpus.Adapters.Text"},
    "tests/RulesCorpus.Cli.Tests": {
        "src/RulesCorpus.Cli", "src/RulesCorpus", "src/RulesCorpus.Adapters.Text",
    },
}

# The core is BCL only. The analyzer is build-time and ships nothing.
CORE_PROJECT = "src/RulesCorpus"
CORE_ALLOWED_PACKAGES = {"Microsoft.CodeAnalysis.PublicApiAnalyzers"}
PUBLIC_API_ANALYZER = "Microsoft.CodeAnalysis.PublicApiAnalyzers"

# ------------------------------------------------------------------------ neutrality
#
# The known consumers' vocabulary: regulatory (eCFR, FAA Part 107, FRCP), tabletop (the
# SRD), and games (Hoyle, backgammon). Matched case-insensitively against whole words AND
# against the camelCase parts of identifiers, so `CombatAdapter` and `Part107Loader` are
# caught as well as `// combat`. This catches accidents, not intent (module docstring).
NEUTRALITY_DENY = {
    "cfr", "ecfr", "faa", "part107", "srd", "dnd", "hoyle", "backgammon", "frcp",
    "spell", "spells", "combat", "aircraft", "drone", "drones", "uas", "waiver", "waivers",
    "regulation", "regulations", "regulatory", "statute", "statutes", "monster", "monsters",
    "weapon", "weapons",
}
NEUTRALITY_SUFFIXES = {".cs", ".csproj"}
WORD = re.compile(r"[A-Za-z0-9]+")
CAMEL_PART = re.compile(r"[A-Z]+(?![a-z])[0-9]*|[A-Z]?[a-z]+[0-9]*|[0-9]+")

# ----------------------------------------------------------------------- determinism
#
# (label, pattern, why). Matched against code with comments and string-literal contents
# blanked (strip_cs_noise), so documentation that names a banned API does not trip it.
NETWORK_BANNED: list[tuple[str, str, str]] = [
    ("System.Net", r"\bSystem\s*\.\s*Net\b", "the network is not an input"),
    ("HttpClient", r"\b(HttpClient|WebClient|HttpRequestMessage|HttpWebRequest)\b",
     "the network is not an input"),
    ("Socket", r"\b(Socket|TcpClient|UdpClient|TcpListener|NetworkStream)\b",
     "the network is not an input"),
    ("Dns", r"\bDns\s*\.\s*[A-Za-z_]", "the network is not an input"),
]

DETERMINISM_BANNED: list[tuple[str, str, str]] = [
    ("DateTime clock", r"\bDateTime\s*\.\s*(Now|UtcNow|Today)\b", "an ambient clock is not an input"),
    ("DateTimeOffset clock", r"\bDateTimeOffset\s*\.\s*(Now|UtcNow)\b", "an ambient clock is not an input"),
    ("TimeProvider.System", r"\bTimeProvider\s*\.\s*System\b", "the system clock behind an abstraction"),
    ("Stopwatch", r"\bStopwatch\b", "process timing is not an input"),
    ("new Random", r"\bnew\s+Random\s*\(", "ambient randomness"),
    ("Random.Shared", r"\bRandom\s*\.\s*Shared\b", "ambient randomness"),
    ("RandomNumberGenerator", r"\bRandomNumberGenerator\b", "ambient randomness"),
    ("Guid factory", r"\bGuid\s*\.\s*(NewGuid|CreateVersion7)\s*\(", "a generated Guid is ambient randomness"),
    # The whole type: Environment.NewLine differs by platform, which is a determinism bug.
    ("Environment.", r"\bEnvironment\s*\.\s*[A-Za-z_]", "the environment makes output machine-dependent"),
    ("Process", r"\bProcess\s*\.\s*[A-Za-z_]|\bnew\s+Process\s*\(|\bProcessStartInfo\b",
     "another process is not a declared input"),
    # `using static System.DateTime;` turns DateTime.UtcNow into a bare UtcNow that no rule
    # above matches. Ban the construct rather than guess every bare member name.
    ("using static System.", r"^\s*(global\s+)?using\s+static\s+System\s*\.",
     "a static import of a System type hides ambient members from this check"),
] + NETWORK_BANNED

# Directory -> rules. The CLI reads files and the clock-free parts of the environment it is
# given on purpose (it is the process boundary); it must still never fetch anything.
DETERMINISM_SCOPES: dict[str, list[tuple[str, str, str]]] = {
    "src/RulesCorpus": DETERMINISM_BANNED,
    "src/RulesCorpus.Adapters.Text": DETERMINISM_BANNED,
    "src/RulesCorpus.Cli": NETWORK_BANNED,
}

# ------------------------------------------------------------------------ text hygiene

BIDI_CONTROLS = {
    "\u202a": "LEFT-TO-RIGHT EMBEDDING", "\u202b": "RIGHT-TO-LEFT EMBEDDING",
    "\u202c": "POP DIRECTIONAL FORMATTING", "\u202d": "LEFT-TO-RIGHT OVERRIDE",
    "\u202e": "RIGHT-TO-LEFT OVERRIDE", "\u2066": "LEFT-TO-RIGHT ISOLATE",
    "\u2067": "RIGHT-TO-LEFT ISOLATE", "\u2068": "FIRST STRONG ISOLATE",
    "\u2069": "POP DIRECTIONAL ISOLATE", "\u200e": "LEFT-TO-RIGHT MARK",
    "\u200f": "RIGHT-TO-LEFT MARK", "\u061c": "ARABIC LETTER MARK",
}
ZERO_WIDTH = {
    "\u200b": "ZERO WIDTH SPACE", "\u200c": "ZERO WIDTH NON-JOINER",
    "\u200d": "ZERO WIDTH JOINER", "\u2060": "WORD JOINER",
    "\ufeff": "ZERO WIDTH NO-BREAK SPACE", "\u00ad": "SOFT HYPHEN",
}

# ------------------------------------------------------------------------ action pins

ACTION_PIN = re.compile(r"^[A-Za-z0-9][\w.-]*/[\w.-]+(?:/[\w./-]+)?@[0-9a-f]{40}$")
USES_LINE = re.compile(r"^\s*(?:-\s*)?uses\s*:\s*(?P<value>[^#]+?)\s*(?:#.*)?$")

# --------------------------------------------------------------------- doc references
#
# A backticked token is a repository path when its first segment is one of these, or a
# top-level directory that exists. The fixed list keeps a reference to a deleted top-level
# directory checked; the dynamic part covers a new one without editing this file.
REFERENCE_PREFIXES = {"docs", "tools", "scripts", "src", "tests", "samples", ".github"}
ROOT_FILE_REFERENCES = {
    "CLAUDE.md", "AGENTS.md", "README.md", "LICENSE", "global.json", "NuGet.config",
    "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
    SOLUTION_FILE, ".editorconfig", ".gitignore", ".gitattributes",
}
FENCE = re.compile(r"^\s*(```|~~~)")
CODE_SPAN = re.compile(r"(`+)(.+?)\1")
INLINE_LINK = re.compile(r"!?\[[^\]]*\]\(\s*<?([^()\s<>]+)>?(?:\s+(?:\"[^\"]*\"|'[^']*'))?\s*\)")
REFERENCE_LINK = re.compile(r"^\s{0,3}\[[^\]]+\]:\s*<?(\S+?)>?(?:\s+.*)?$")
URL_SCHEME = re.compile(r"^[A-Za-z][A-Za-z0-9+.-]*:")
PATH_TOKEN = re.compile(r"^(?:\./)?[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]*)*$")


class Failure(str):
    """A single human-readable check failure."""


@dataclass
class CheckResult:
    failures: list[Failure] = field(default_factory=list)
    examined: int = 0

    def fail(self, message: str) -> None:
        self.failures.append(Failure(message))


# ---------------------------------------------------------------------- file discovery


def _ignored(rel: Path) -> bool:
    return any(part in IGNORED_PARTS for part in rel.parts)


def repo_files(root: Path) -> list[Path]:
    """Every file git would put in the next commit: tracked, or untracked and not ignored.

    Untracked files are included because the gate runs before `git add`. When the root is
    not a git checkout, or is one with nothing tracked yet, the working tree is walked
    instead -- examining nothing and reporting success is the one outcome not allowed.
    """
    collected: set[Path] = set()
    for args in (["git", "ls-files", "-z"],
                 ["git", "ls-files", "-z", "--others", "--exclude-standard"]):
        try:
            done = subprocess.run(args, cwd=root, capture_output=True, text=True, check=False)
        except OSError:
            break
        if done.returncode != 0:
            break
        for name in done.stdout.split("\0"):
            path = root / name
            if name and path.is_file() and not path.is_symlink():
                collected.add(path)
    if collected:
        return sorted(collected)
    return sorted(
        p for p in root.rglob("*")
        if p.is_file() and not p.is_symlink() and not _ignored(p.relative_to(root))
    )


def is_defect_fixture(rel: Path) -> bool:
    """A file inside a tests/**/invalid*/ directory: deliberately malformed input."""
    parts = rel.parts
    return (len(parts) > 2 and parts[0] == "tests"
            and any(part.lower().startswith("invalid") for part in parts[1:-1]))


def is_probably_text(path: Path, raw: bytes) -> bool:
    if path.suffix.lower() in BINARY_SUFFIXES:
        return False
    if path.suffix in TEXT_SUFFIXES or path.name in TEXT_NAMES:
        return True
    return b"\0" not in raw[:8192]


def all_csproj(root: Path) -> list[Path]:
    """Every csproj that belongs to this repository, at any depth."""
    return [p for p in repo_files(root) if p.suffix == ".csproj"]


def cs_files_under(directory: Path) -> list[Path]:
    if not directory.is_dir():
        return []
    return [p for p in sorted(directory.rglob("*.cs")) if not _ignored(p.relative_to(directory))]


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_project(path: Path) -> ElementTree.Element | None:
    try:
        return ElementTree.parse(path).getroot()
    except (ElementTree.ParseError, OSError):
        return None


def item_includes(project: ElementTree.Element, item: str) -> list[str]:
    return [e.get("Include", "") for e in project.iter() if _local(e.tag) == item]


def property_is(project: ElementTree.Element, name: str, value: str) -> bool:
    return any(_local(e.tag) == name and (e.text or "").strip().lower() == value
               for e in project.iter())


# ------------------------------------------------------------------ the C# text lexer


def strip_cs_noise(text: str) -> str:
    """Blank out comment bodies and string-literal contents, preserving offsets.

    Every character that is not executable code becomes a space; newlines are kept, so line
    and column numbers match the original. Interpolation holes are code: `$"{DateTime.Now}"`
    is a call. It is a lexer, not a parser: it knows regular, verbatim, raw and interpolated
    strings, char literals and both comment forms; preprocessor lines are left as code,
    which is the safe direction. Ported from rules-kernel's tools/repo-checks.py.
    """
    out: list[str] = []
    i, n = 0, len(text)
    mode = "code"
    interp = verbatim = False
    raw_quotes = 0
    brace_depth = 0
    stack: list[tuple[bool, bool, int]] = []

    def push(ch: str) -> None:
        out.append(ch if ch == "\n" else " ")

    while i < n:
        ch = text[i]
        if mode == "code":
            if ch == "/" and i + 1 < n and text[i + 1] == "/":
                mode = "line"
                push(ch)
                i += 1
                continue
            if ch == "/" and i + 1 < n and text[i + 1] == "*":
                mode = "block"
                push(ch)
                push("*")
                i += 2
                continue
            if ch == "'":
                mode = "char"
                push(ch)
                i += 1
                continue
            if ch == '"':
                j, prefix = i - 1, ""
                while j >= 0 and text[j] in "@$":
                    prefix = text[j] + prefix
                    j -= 1
                verbatim, interp = "@" in prefix, "$" in prefix
                quotes = 0
                while i + quotes < n and text[i + quotes] == '"':
                    quotes += 1
                if quotes >= 3:
                    mode, raw_quotes = "raw", quotes
                    for _ in range(quotes):
                        push('"')
                    i += quotes
                    continue
                mode, raw_quotes = "string", 0
                push(ch)
                i += 1
                continue
            if stack:
                if ch == "{":
                    brace_depth += 1
                elif ch == "}":
                    brace_depth -= 1
                    if brace_depth == 0:
                        interp, verbatim, raw_quotes = stack.pop()
                        mode = "raw" if raw_quotes else "string"
                        push(ch)
                        i += 1
                        continue
            out.append(ch)
            i += 1
            continue

        if mode == "line":
            if ch == "\n":
                mode = "code"
            push(ch)
            i += 1
            continue

        if mode == "block":
            if ch == "*" and i + 1 < n and text[i + 1] == "/":
                mode = "code"
                push("*")
                push("/")
                i += 2
                continue
            push(ch)
            i += 1
            continue

        if mode == "char":
            if ch == "\\" and i + 1 < n:
                push(ch)
                push(text[i + 1])
                i += 2
                continue
            if ch in ("'", "\n"):
                mode = "code"
            push(ch)
            i += 1
            continue

        if mode == "string":
            if interp and ch == "{":
                if i + 1 < n and text[i + 1] == "{":
                    push(ch)
                    push("{")
                    i += 2
                    continue
                stack.append((interp, verbatim, raw_quotes))
                mode, brace_depth = "code", 1
                push(ch)
                i += 1
                continue
            if verbatim:
                if ch == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        push(ch)
                        push('"')
                        i += 2
                        continue
                    mode = "code"
                push(ch)
                i += 1
                continue
            if ch == "\\" and i + 1 < n:
                push(ch)
                push(text[i + 1])
                i += 2
                continue
            if ch in ('"', "\n"):
                mode = "code"
            push(ch)
            i += 1
            continue

        if mode == "raw":
            if interp and ch == "{":
                stack.append((interp, verbatim, raw_quotes))
                mode, brace_depth = "code", 1
                push(ch)
                i += 1
                continue
            if ch == '"':
                quotes = 0
                while i + quotes < n and text[i + quotes] == '"':
                    quotes += 1
                if quotes >= raw_quotes:
                    mode, raw_quotes = "code", 0
                for _ in range(quotes):
                    push('"')
                i += quotes
                continue
            push(ch)
            i += 1
            continue

    return "".join(out)


# --------------------------------------------------------------------------- checks


def check_text_hygiene(root: Path) -> CheckResult:
    """Two people reading the same bytes see the same text.

    Exempt: binary files (the founding .docx), defect fixtures, and the trailing-newline
    rule on packages.lock.json, which NuGet rewrites without one on every restore.
    """
    result = CheckResult()
    for path in repo_files(root):
        rel = path.relative_to(root)
        if is_defect_fixture(rel):
            continue
        raw = path.read_bytes()
        if not is_probably_text(path, raw):
            continue
        result.examined += 1
        if raw.startswith(b"\xef\xbb\xbf"):
            result.fail(f"{rel}: UTF-8 byte-order mark")
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError as error:
            result.fail(f"{rel}: not valid UTF-8 ({error.reason} at byte {error.start})")
            continue
        if b"\r\n" in raw:
            result.fail(f"{rel}: CRLF line endings (LF required)")
        elif b"\r" in raw:
            result.fail(f"{rel}: lone CR line endings (LF required)")
        if path.name != "packages.lock.json" and raw:
            if not raw.endswith(b"\n"):
                result.fail(f"{rel}: missing trailing newline")
            elif raw.endswith(b"\n\n"):
                result.fail(f"{rel}: more than one trailing newline")

        for lineno, line in enumerate(text.split("\n"), 1):
            for column, char in enumerate(line, 1):
                if lineno == 1 and column == 1 and char == "\ufeff":
                    continue  # reported above as a BOM
                name = BIDI_CONTROLS.get(char) or ZERO_WIDTH.get(char)
                if name:
                    result.fail(f"{rel}:{lineno}:{column}: invisible or bidirectional "
                                f"character U+{ord(char):04X} ({name})")
                    break

        # Identifiers must be ASCII so two distinct symbols cannot render identically.
        # Comments and strings are exempt; they are not what the compiler binds.
        if path.suffix == ".cs":
            for lineno, code in enumerate(strip_cs_noise(text).split("\n"), 1):
                for column, char in enumerate(code, 1):
                    if ord(char) > 127:
                        result.fail(f"{rel}:{lineno}:{column}: non-ASCII U+{ord(char):04X} in code")
                        break
    return result


def _check_yaml(path: Path, rel: Path, result: CheckResult) -> None:
    try:
        import yaml  # noqa: PLC0415 -- the ImportError path below is the point
    except ImportError:
        result.fail(f"{rel}: YAML NOT verified -- PyYAML is not importable. A malformed "
                    "workflow does not error on GitHub; it silently does not run. Install: "
                    "python3 -m pip install --require-hashes -r tools/requirements.txt")
        return
    try:
        list(yaml.safe_load_all(path.read_bytes()))
    except yaml.YAMLError as error:
        result.fail(f"{rel}: not well-formed YAML: {error}")


def check_parseable(root: Path) -> CheckResult:
    """Every XML, YAML and JSON file parses.

    A malformed Directory.Build.props surfaces in MSBuild as a symptom several layers away;
    a malformed workflow is not an error at all, it just stops running. Defect fixtures
    (tests/**/invalid*/) are exempt. Proves well-formedness only, not correctness.
    """
    result = CheckResult()
    for path in repo_files(root):
        rel = path.relative_to(root)
        if is_defect_fixture(rel):
            continue
        suffix = path.suffix.lower()
        if suffix in XML_SUFFIXES:
            result.examined += 1
            parser = xml.parsers.expat.ParserCreate()
            try:
                parser.Parse(path.read_bytes(), True)
            except xml.parsers.expat.ExpatError as error:
                result.fail(f"{rel}: not well-formed XML: {error}")
        elif suffix in YAML_SUFFIXES:
            result.examined += 1
            _check_yaml(path, rel, result)
        elif suffix == ".json":
            result.examined += 1
            try:
                json.loads(path.read_bytes().decode("utf-8"))
            except (json.JSONDecodeError, UnicodeDecodeError) as error:
                result.fail(f"{rel}: not well-formed JSON: {error}")
    return result


def check_layering(root: Path) -> CheckResult:
    """Every csproj's ProjectReferences are a subset of what ALLOWED_PROJECT_REFS declares.

    Also: the core references no package but the public-API analyzer, and no
    Directory.Build.props/.targets or Directory.Packages.props injects a reference into
    every project, where reading csproj files cannot see it.
    """
    result = CheckResult()
    projects: dict[str, Path] = {}
    for csproj in all_csproj(root):
        rel = csproj.relative_to(root)
        key = rel.parent.as_posix()
        result.examined += 1
        if key not in ALLOWED_PROJECT_REFS:
            result.fail(f"{rel}: not declared in ALLOWED_PROJECT_REFS in tools/repo-checks.py; "
                        "an undeclared project escapes layering enforcement")
            continue
        if csproj.stem != rel.parent.name:
            result.fail(f"{rel}: a declared project directory must contain "
                        f"{rel.parent.name}.csproj")
            continue
        projects[key] = csproj

    for key in sorted(ALLOWED_PROJECT_REFS):
        if key not in projects:
            result.fail(f"{key}: declared in ALLOWED_PROJECT_REFS but has no csproj on disk")

    for key, csproj in sorted(projects.items()):
        rel = csproj.relative_to(root)
        project = parse_project(csproj)
        if project is None:
            result.fail(f"{rel}: cannot be parsed, so its references cannot be read")
            continue
        for include in item_includes(project, "ProjectReference"):
            target = (csproj.parent / include.replace("\\", "/")).resolve()
            try:
                target_key = target.relative_to(root.resolve()).parent.as_posix()
            except ValueError:
                result.fail(f"{rel}: references '{include}', outside the repository")
                continue
            if target_key not in ALLOWED_PROJECT_REFS[key]:
                result.fail(f"{rel}: references '{target_key}', which ALLOWED_PROJECT_REFS "
                            "does not allow")
        if key == CORE_PROJECT:
            for package in item_includes(project, "PackageReference"):
                if package not in CORE_ALLOWED_PACKAGES:
                    result.fail(f"{rel}: PackageReference '{package}'; the core is BCL only")

    for pattern in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"):
        for props in sorted(root.rglob(pattern)):
            rel = props.relative_to(root)
            if _ignored(rel):
                continue
            project = parse_project(props)
            if project is None:
                continue  # parseable reports it
            for item in ("ProjectReference", "PackageReference", "GlobalPackageReference"):
                if item_includes(project, item):
                    result.fail(f"{rel}: declares a {item}, which MSBuild injects into every "
                                "project beneath it where this check cannot see it; declare "
                                "references in the csproj")
    return result


def check_solution_membership(root: Path) -> CheckResult:
    """Every csproj on disk is in the solution, and every solution entry exists.

    A project missing from the solution is never built or tested, and the gate stays green.
    """
    result = CheckResult()
    on_disk = {p.relative_to(root).as_posix() for p in all_csproj(root)}
    solution = root / SOLUTION_FILE
    if not solution.is_file():
        result.fail(f"{SOLUTION_FILE}: does not exist")
        return result
    tree = parse_project(solution)
    if tree is None:
        result.fail(f"{SOLUTION_FILE}: not well-formed XML")
        return result
    listed = {
        (e.get("Path") or "").replace("\\", "/").removeprefix("./")
        for e in tree.iter() if _local(e.tag) == "Project"
    } - {""}
    result.examined = len(on_disk | listed)
    for missing in sorted(on_disk - listed):
        result.fail(f"{missing}: on disk but not in {SOLUTION_FILE}; the gate never builds it")
    for dangling in sorted(listed - on_disk):
        result.fail(f"{SOLUTION_FILE}: lists '{dangling}', which is not on disk")
    return result


def neutrality_hits(line: str) -> list[str]:
    """Deny-listed words on a line, as whole words or as camelCase identifier parts."""
    hits: list[str] = []
    for token in WORD.findall(line):
        candidates = [token] + CAMEL_PART.findall(token)
        for candidate in candidates:
            word = candidate.lower()
            if word in NEUTRALITY_DENY and word not in hits:
                hits.append(word)
    return hits


def check_neutrality(root: Path) -> CheckResult:
    """No known consumer's domain vocabulary anywhere in src/ -- code, comments or strings.

    A deny list catches accidents, not intent. It names the vocabulary of the consumers we
    know about; a new consumer's words, a synonym, or a word split across a string
    concatenation all pass. CLAUDE.md principle 1 is therefore also a review obligation.
    """
    result = CheckResult()
    for path in repo_files(root):
        rel = path.relative_to(root)
        if rel.parts[0] != "src" or path.suffix not in NEUTRALITY_SUFFIXES:
            continue
        result.examined += 1
        text = path.read_text(encoding="utf-8", errors="replace")
        for lineno, line in enumerate(text.split("\n"), 1):
            for word in neutrality_hits(line):
                result.fail(f"{rel}:{lineno}:{word}")
    return result


def check_determinism(root: Path) -> CheckResult:
    """The core and the text adapter read no clock, randomness, environment, process or
    network; the CLI reaches no network.

    Matching runs over strip_cs_noise output, so a comment or string that names a banned
    API passes. A pattern deny list: see the module docstring for what it cannot catch.
    """
    result = CheckResult()
    for scope, rules in DETERMINISM_SCOPES.items():
        for path in cs_files_under(root / scope):
            result.examined += 1
            rel = path.relative_to(root)
            raw_lines = path.read_text(encoding="utf-8", errors="replace").split("\n")
            code_lines = strip_cs_noise("\n".join(raw_lines)).split("\n")
            for lineno, code in enumerate(code_lines, 1):
                for label, pattern, why in rules:
                    if re.search(pattern, code):
                        result.fail(f"{rel}:{lineno}: {label} -- {why}  "
                                    f"[{raw_lines[lineno - 1].strip()[:70]}]")
    return result


def check_action_pins(root: Path) -> CheckResult:
    """Every `uses:` in .github/workflows is pinned to a 40-hex commit SHA.

    A tag is a pointer its owner can move. Local (`./`) and `docker://` actions are exempt.
    Read as text so failures carry line numbers; parseable proves the YAML is valid.
    """
    result = CheckResult()
    workflows = root / ".github" / "workflows"
    if not workflows.is_dir():
        return result
    for path in sorted(workflows.iterdir()):
        if path.suffix.lower() not in YAML_SUFFIXES or not path.is_file():
            continue
        rel = path.relative_to(root)
        for lineno, line in enumerate(path.read_text(encoding="utf-8").split("\n"), 1):
            match = USES_LINE.match(line)
            if not match:
                continue
            value = match.group("value").strip().strip("'\"")
            result.examined += 1
            if value.startswith("./") or value.startswith("docker://"):
                continue
            if not ACTION_PIN.fullmatch(value):
                result.fail(f"{rel}:{lineno}: '{value}' is not pinned to a 40-hex commit SHA")
    return result


def _markdown_prose_and_code(text: str) -> tuple[list[tuple[int, str]], list[tuple[int, str]]]:
    """Split a markdown file into (prose lines with code spans removed, inline code spans).

    Fenced blocks are neither: a fenced block is an example or a diagram, and its paths are
    often illustrative (`<corpus>/corpus.json`). A link inside code is not a link.
    """
    prose: list[tuple[int, str]] = []
    spans: list[tuple[int, str]] = []
    fence: str | None = None
    for lineno, line in enumerate(text.split("\n"), 1):
        match = FENCE.match(line)
        if match:
            if fence is None:
                fence = match.group(1)
            elif match.group(1) == fence:
                fence = None
            continue
        if fence is not None:
            continue
        for span in CODE_SPAN.finditer(line):
            spans.append((lineno, span.group(2).strip()))
        prose.append((lineno, CODE_SPAN.sub(" ", line)))
    return prose, spans


def _inside(path: Path, root: Path) -> bool:
    try:
        path.resolve().relative_to(root.resolve())
        return True
    except ValueError:
        return False


def check_doc_references(root: Path) -> CheckResult:
    """Every relative markdown link and backticked repository path in a .md file resolves.

    Links resolve against the document's directory (a leading `/` against the root); URLs
    and anchor-only links are ignored, and `#anchor` and `?query` are stripped -- anchors
    are not verified. A backticked token is checked when it is path-shaped and starts with a
    known top-level directory or names a known root file; it resolves against the root.
    Fenced code blocks and tokens containing glob or placeholder characters are ignored.
    What it does NOT prove: that the cited file is the right one.
    """
    result = CheckResult()
    top_level = {p.name for p in root.iterdir() if p.is_dir() and p.name not in IGNORED_PARTS}
    prefixes = REFERENCE_PREFIXES | top_level
    for path in repo_files(root):
        if path.suffix.lower() != ".md":
            continue
        rel = path.relative_to(root)
        result.examined += 1
        prose, spans = _markdown_prose_and_code(path.read_text(encoding="utf-8", errors="replace"))

        for lineno, line in prose:
            targets = INLINE_LINK.findall(line)
            ref = REFERENCE_LINK.match(line)
            if ref:
                targets.append(ref.group(1))
            for target in targets:
                if URL_SCHEME.match(target) or target.startswith(("#", "//")):
                    continue
                clean = urllib.parse.unquote(re.split(r"[#?]", target, maxsplit=1)[0])
                if not clean:
                    continue
                resolved = (root / clean.lstrip("/")) if clean.startswith("/") else (path.parent / clean)
                if not _inside(resolved, root):
                    result.fail(f"{rel}:{lineno}: link '{target}' escapes the repository")
                elif not resolved.exists():
                    result.fail(f"{rel}:{lineno}: link '{target}' does not resolve")

        for lineno, span in spans:
            for token in span.split():
                token = token.rstrip(".,;:)")
                if not PATH_TOKEN.match(token):
                    continue
                bare = token.removeprefix("./")
                first = bare.split("/", 1)[0]
                if not (("/" in bare and first in prefixes) or bare in ROOT_FILE_REFERENCES):
                    continue
                if not (root / bare).exists():
                    result.fail(f"{rel}:{lineno}: `{token}` does not exist in this repository")
    return result


def check_public_api(root: Path) -> CheckResult:
    """Every packable library in src/ declares its public surface.

    Packable means not IsPackable=false and not a .NET tool (PackAsTool=true): an executable
    has no API surface for consumers. Proves the baselines and analyzer are present, not
    that the baselines are accurate -- the analyzer does that at build time.
    """
    result = CheckResult()
    for csproj in all_csproj(root):
        rel = csproj.relative_to(root)
        if rel.parts[0] != "src":
            continue
        project = parse_project(csproj)
        if project is None:
            result.fail(f"{rel}: cannot be parsed")
            continue
        if property_is(project, "IsPackable", "false") or property_is(project, "PackAsTool", "true"):
            continue
        result.examined += 1
        for baseline in ("PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt"):
            if not (csproj.parent / baseline).is_file():
                result.fail(f"{rel}: packable library has no {baseline}")
        if PUBLIC_API_ANALYZER not in item_includes(project, "PackageReference"):
            result.fail(f"{rel}: packable library does not reference {PUBLIC_API_ANALYZER}")
    return result


CHECKS = {
    "text-hygiene": check_text_hygiene,
    "parseable": check_parseable,
    "layering": check_layering,
    "solution-membership": check_solution_membership,
    "neutrality": check_neutrality,
    "determinism": check_determinism,
    "action-pins": check_action_pins,
    "doc-references": check_doc_references,
    "public-api": check_public_api,
}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="repo-checks.py", description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=str(ROOT), help="repository root (default: this checkout)")
    parser.add_argument("--only", action="append", choices=sorted(CHECKS), default=None)
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--allow-skip", action="store_true",
                        help="do not fail when a check examined nothing; for synthetic trees "
                             "in tools/tests only, never for the gate")
    args = parser.parse_args(argv)

    root = Path(args.root).resolve()
    names = args.only or list(CHECKS)
    results = {name: CHECKS[name](root) for name in names}
    failures = sum(len(r.failures) for r in results.values())
    skipped = [name for name, r in results.items() if r.examined == 0]
    skip_fails = bool(skipped) and not args.allow_skip
    exit_code = 1 if (failures or skip_fails) else 0

    if args.json:
        print(json.dumps({
            "exit": exit_code,
            "failures": failures,
            "skipped": skipped,
            "skip_fails_run": skip_fails,
            "checks": {
                name: {"examined": r.examined, "failures": [str(f) for f in r.failures]}
                for name, r in results.items()
            },
        }, indent=2))
        return exit_code

    for name in names:
        r = results[name]
        if r.failures:
            print(f"FAIL  {name}  ({len(r.failures)})")
            for problem in r.failures:
                print(f"        {problem}")
        elif r.examined == 0:
            print(f"skip  {name}  (nothing in scope -- this check proved nothing)")
        else:
            print(f"ok    {name}  ({r.examined} examined)")
    print()
    if skipped:
        verdict = "FAIL" if skip_fails else "allowed by --allow-skip"
        print(f"repo-checks: {len(skipped)} check(s) examined nothing ({', '.join(skipped)}) -- {verdict}")
    if failures:
        print(f"repo-checks: {failures} failure(s)")
    elif skip_fails:
        print("repo-checks: FAIL (a check that proved nothing cannot report PASS)")
    else:
        print("repo-checks: PASS")
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
