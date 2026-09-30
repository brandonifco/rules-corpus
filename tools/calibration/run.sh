#!/usr/bin/env bash
# run.sh -- calibrate rules-corpus against one of its real consumers (plan milestone M4).
#
#   tools/calibration/run.sh <consumer-name> [--repo <path>] [--corpus-file <path>]
#
# NOT part of scripts/validate.sh: it reads a local git checkout of the consumer (default
# ~/<consumer-name>), which the gate cannot assume exists. Run it by hand; the record of a run
# goes in docs/calibration/.
#
# For the consumer named in tools/calibration/consumers.json it:
#   1. checks the pinned commit exists in the checkout, extracts the consumer's corpus file AT
#      THAT COMMIT (`git show <commit>:<path>`, so a checkout that has moved on does not change
#      the result), and checks the provenance.json at that commit still records the pinned
#      baseline values;
#   2. checks the extracted bytes' SHA-256 against the pinned contentHash, independently of
#      rules-corpus;
#   3. builds the rules-corpus CLI from this checkout, writes the consumer's build definition
#      (tools/calibration/<name>.corpus.build.json) and the extracted bytes into a fresh
#      temporary directory, and runs `build` (nothing built is ever committed: the manifest
#      format may change under this tool, so every run builds fresh);
#   4. asserts the manifest's baseline projection -- sourceId, the named artifact's digest hex,
#      hashDerivation, asOf (absent = null) -- equals the pinned values exactly;
#   5. reports the segment count (and asserts it where consumers.json pins one);
#   6. runs `verify --rebuild` and checks the exit code consumers.json expects; where that is
#      3 (not verified), also that `--allow-not-verified` turns it into 0;
#   7. builds a second fresh copy and requires every built file to be byte-identical.
#
# --corpus-file <path> takes the corpus bytes from a file instead of from the pinned commit,
# for a machine where the checkout's git history cannot be read. Step 1's checks are then
# reported "not-verified" and the run exits 3 at best: it is not a calibration against the pin.
#
# Needs git, python3 and the .NET SDK; assumes `dotnet restore` has run; needs no network.
# Prints ok / FAIL / not-verified lines. Exit codes: 0 every check ok; 1 any FAIL; 2 usage
# error; 3 no FAIL but some checks not verified (only with --corpus-file).
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"
CONSUMERS="tools/calibration/consumers.json"

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if [[ -t 1 ]]; then
  RED=$'\033[31m'; GREEN=$'\033[32m'; YEL=$'\033[33m'; OFF=$'\033[0m'
else
  RED=""; GREEN=""; YEL=""; OFF=""
fi

FAILED=0
NOT_VERIFIED=0
ok() { printf '%sok%s   %s\n' "$GREEN" "$OFF" "$1"; }
fail() { printf '%sFAIL%s %s\n' "$RED" "$OFF" "$1"; FAILED=1; }
notverified() { printf '%snot-verified%s %s\n' "$YEL" "$OFF" "$1"; NOT_VERIFIED=1; }
info() { printf '     %s\n' "$1"; }
usage() { echo "usage: tools/calibration/run.sh <consumer-name> [--repo <path>] [--corpus-file <path>]" >&2; exit 2; }
now_ms() { date +%s%3N; }

# ------------------------------------------------------------------------------ arguments
[[ $# -ge 1 ]] || usage
NAME="$1"; shift
REPO=""
CORPUS_FILE=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) [[ $# -ge 2 ]] || usage; REPO="$2"; shift 2 ;;
    --corpus-file) [[ $# -ge 2 ]] || usage; CORPUS_FILE="$2"; shift 2 ;;
    *) usage ;;
  esac
done
[[ -n "$REPO" ]] || REPO="$HOME/$NAME"

# One consumer's entry, as shell-quoted assignments (python writes them; values are data).
if ! ENTRY="$(python3 - "$CONSUMERS" "$NAME" <<'PY'
import json, shlex, sys
data = json.load(open(sys.argv[1], encoding="utf-8"))
matches = [c for c in data["consumers"] if c["name"] == sys.argv[2]]
if len(matches) != 1:
    names = ", ".join(c["name"] for c in data["consumers"])
    sys.exit(f"no consumer named {sys.argv[2]!r} in {sys.argv[1]} (have: {names})")
c = matches[0]
build = json.load(open(c["buildDefinition"], encoding="utf-8"))
paths = [s["path"] for s in build["sources"] if s["id"] == c["corpusArtifact"] and s.get("stored", True)]
if len(paths) != 1:
    sys.exit(f"{c['buildDefinition']}: no stored source {c['corpusArtifact']!r}")
seg = c["segments"]
for key, value in (("COMMIT", c["commit"]), ("CORPUS_PATH", c["corpusPath"]),
                   ("PROVENANCE_PATH", c["provenancePath"]), ("BUILD_DEF", c["buildDefinition"]),
                   ("ARTIFACT", c["corpusArtifact"]), ("SOURCE_PATH", paths[0]),
                   ("EXPECTED_HASH", c["expected"]["contentHash"]),
                   ("EXPECTED_SEGMENTS", "" if seg is None else str(seg)),
                   ("VERIFY_EXIT", str(c["verifyRebuildExit"]))):
    print(f"{key}={shlex.quote(value)}")
PY
)"; then
  exit 2
fi
eval "$ENTRY"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "-- calibrate $NAME against $COMMIT (checkout: $REPO)"

# ----------------------------------------------------- 1. the corpus bytes at the pinned commit
BYTES="$WORK/corpus-bytes"
if [[ -n "$CORPUS_FILE" ]]; then
  if ! cp "$CORPUS_FILE" "$BYTES" 2>/dev/null; then
    fail "read --corpus-file $CORPUS_FILE"
    exit 1
  fi
  notverified "pinned commit $COMMIT: --corpus-file given, so the bytes are not taken from it"
  notverified "provenance at the pinned commit: not read (--corpus-file given)"
else
  if git -C "$REPO" cat-file -e "$COMMIT^{commit}" 2>/dev/null; then
    ok "pinned commit $COMMIT exists in $REPO"
  else
    fail "pinned commit $COMMIT is not in $REPO"
    exit 1
  fi
  HEAD_NOW="$(git -C "$REPO" rev-parse HEAD 2>/dev/null || echo unknown)"
  [[ "$HEAD_NOW" == "$COMMIT" ]] || info "note: the checkout's HEAD is $HEAD_NOW; reading the pinned commit regardless"
  if git -C "$REPO" show "$COMMIT:$CORPUS_PATH" >"$BYTES" 2>"$WORK/git.err"; then
    ok "extract $CORPUS_PATH at the pinned commit ($(stat -c %s "$BYTES") bytes)"
  else
    sed 's/^/     /' "$WORK/git.err"
    fail "extract $CORPUS_PATH at the pinned commit"
    exit 1
  fi
  if git -C "$REPO" show "$COMMIT:$PROVENANCE_PATH" >"$WORK/provenance.json" 2>"$WORK/git.err"; then
    if python3 - "$CONSUMERS" "$NAME" "$WORK/provenance.json" <<'PY'
import json, sys
consumer = next(c for c in json.load(open(sys.argv[1], encoding="utf-8"))["consumers"] if c["name"] == sys.argv[2])
corpus = json.load(open(sys.argv[3], encoding="utf-8")).get("corpus") or {}
bad = [f"{k}: provenance {corpus.get(k)!r}, pinned {v!r}"
       for k, v in consumer["expected"].items() if k not in corpus or corpus[k] != v]
for line in bad:
    print("     " + line)
sys.exit(1 if bad else 0)
PY
    then
      ok "$PROVENANCE_PATH at the pinned commit records the pinned baseline values"
    else
      fail "$PROVENANCE_PATH at the pinned commit does not record the pinned baseline values"
    fi
  else
    sed 's/^/     /' "$WORK/git.err"
    fail "extract $PROVENANCE_PATH at the pinned commit"
  fi
fi

# ------------------------------------------------ 2. the bytes' digest, independently of the CLI
ACTUAL_HASH="$(sha256sum "$BYTES" | cut -d' ' -f1)"
if [[ "$ACTUAL_HASH" == "$EXPECTED_HASH" ]]; then
  ok "sha256 of the corpus bytes is the pinned contentHash $EXPECTED_HASH"
else
  fail "sha256 of the corpus bytes is $ACTUAL_HASH, pinned contentHash $EXPECTED_HASH"
fi

# -------------------------------------------------------------------- 3. the CLI, then build
CLI_DIR="$WORK/cli"
if ! dotnet build src/RulesCorpus.Cli/RulesCorpus.Cli.csproj -c Release --no-restore --nologo \
    -warnaserror -o "$CLI_DIR" >"$WORK/cli-build.log" 2>&1 || [[ ! -f "$CLI_DIR/rules-corpus.dll" ]]; then
  tail -30 "$WORK/cli-build.log"
  fail "build the rules-corpus CLI"
  exit 1
fi
ok "build the rules-corpus CLI"
cli() { dotnet "$CLI_DIR/rules-corpus.dll" "$@"; }

# expect <exit code> <label> <cli arguments...>: runs the CLI and reports its exit and time.
expect() {
  local want="$1" label="$2"; shift 2
  local got=0 start end
  start="$(now_ms)"
  cli "$@" >"$WORK/last.out" 2>"$WORK/last.err" || got=$?
  end="$(now_ms)"
  if [[ "$got" -eq "$want" ]]; then
    ok "$label (exit $got, $((end - start)) ms)"
    return 0
  fi
  grep -hv '^ok ' "$WORK/last.out" "$WORK/last.err" | tail -20 | sed 's/^/     /'
  fail "$label: exit $got, expected $want"
  return 1
}

# fresh_corpus <dir>: the build definition and the extracted bytes at the source's path.
fresh_corpus() {
  mkdir -p "$1/$(dirname "$SOURCE_PATH")"
  cp "$BUILD_DEF" "$1/corpus.build.json"
  cp "$BYTES" "$1/$SOURCE_PATH"
}

ONE="$WORK/corpus-1"
fresh_corpus "$ONE"
expect 0 "build" build --dir "$ONE" || exit 1
sed 's/^/     /' "$WORK/last.out"

# ------------------------------------------------------- 4 and 5. baseline projection, segments
python3 - "$CONSUMERS" "$NAME" "$ONE/corpus.json" "$EXPECTED_SEGMENTS" <<'PY' \
  | sed -e "s/^ok   /${GREEN}ok${OFF}   /" -e "s/^FAIL /${RED}FAIL${OFF} /"
import collections, json, sys
consumer = next(c for c in json.load(open(sys.argv[1], encoding="utf-8"))["consumers"] if c["name"] == sys.argv[2])
manifest = json.load(open(sys.argv[3], encoding="utf-8"))
expected = consumer["expected"]
failed = False
def report(good, text):
    global failed
    print(("ok   " if good else "FAIL ") + text)
    failed = failed or not good

baselines = [b for b in manifest["baselines"] if b["sourceId"] == expected["sourceId"]]
report(len(baselines) == 1, f"one baseline with sourceId {expected['sourceId']} ({len(baselines)} found)")
if len(baselines) == 1:
    b = baselines[0]
    artifacts = [a for a in manifest["artifacts"] if a["id"] == b["artifact"]]
    digest = artifacts[0]["digest"] if len(artifacts) == 1 else None
    hex_part = digest[len("sha256:"):] if digest and digest.startswith("sha256:") else None
    projection = {"sourceId": b["sourceId"], "contentHash": hex_part,
                  "hashDerivation": b["hashDerivation"], "asOf": b.get("asOf")}
    for key in ("sourceId", "contentHash", "hashDerivation", "asOf"):
        report(projection[key] == expected[key],
               f"baseline {key}: manifest {json.dumps(projection[key])}, pinned {json.dumps(expected[key])}")
    report(projection == expected, f"baseline projection equals the pinned values exactly (artifact {b['artifact']})")

segments = manifest["segments"]
pages = collections.OrderedDict()
for s in segments:
    for span in s.get("sources", []):
        if "pages" in span:
            pages[span["pages"]["from"]] = True
            pages[span["pages"]["to"]] = True
detail = f"{len(segments)} segment(s)"
if pages:
    detail += f", on {len(pages)} page(s) {min(pages)}-{max(pages)}"
want = sys.argv[4]
if want:
    report(len(segments) == int(want), f"segments: {detail}, pinned {want}")
else:
    print(f"     segments: {detail} (reported, not pinned)")
print(f"     contentDigest  {manifest['contentDigest']}")
print(f"     manifestDigest {manifest['manifestDigest']}")
sys.exit(1 if failed else 0)
PY
[[ "${PIPESTATUS[0]}" -eq 0 ]] || FAILED=1

# --------------------------------------------------------------------------- 6. verify --rebuild
if expect "$VERIFY_EXIT" "verify --rebuild" verify "$ONE" --rebuild; then
  grep -h '^not-verified' "$WORK/last.out" | sed 's/^/     /'
fi
if [[ "$VERIFY_EXIT" -eq 3 ]]; then
  expect 0 "verify --rebuild --allow-not-verified" verify "$ONE" --rebuild --allow-not-verified
fi

# --------------------------------------------------------------- 7. a second build, byte for byte
TWO="$WORK/corpus-2"
fresh_corpus "$TWO"
if expect 0 "build a second fresh copy" build --dir "$TWO"; then
  DIFFERENT="$(cd "$ONE" && find . -type f | sort | while IFS= read -r path; do
    cmp -s "$ONE/$path" "$TWO/$path" || echo "$path"; done)"
  COUNT="$(cd "$ONE" && find . -type f | wc -l)"
  if [[ -z "$DIFFERENT" && "$COUNT" -eq "$(cd "$TWO" && find . -type f | wc -l)" ]]; then
    ok "the second build is byte-identical to the first ($COUNT file(s))"
  else
    fail "the second build differs from the first: ${DIFFERENT:-file count differs}"
  fi
fi

echo
if [[ "$FAILED" -ne 0 ]]; then
  echo "calibration $NAME: FAIL"
  exit 1
elif [[ "$NOT_VERIFIED" -ne 0 ]]; then
  echo "calibration $NAME: NOT VERIFIED (not a calibration against the pinned commit)"
  exit 3
fi
echo "calibration $NAME: PASS"
exit 0
