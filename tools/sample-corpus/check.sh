#!/usr/bin/env bash
# check.sh -- the one documented command path, run for real against the committed samples.
#
#   tools/sample-corpus/check.sh
#
# Builds the rules-corpus CLI once, then for each corpus under samples/:
#   1. copies its corpus.build.json and stored sources (never its built outputs) into a
#      fresh directory and runs `build`;
#   2. runs `verify --rebuild` and checks the exit code the sample is meant to produce;
#   3. compares every built file byte for byte with the committed sample, so the committed
#      outputs can never drift from what the code produces;
#   4. builds a second fresh copy and compares its bytes with the first;
#   5. packs both and compares the archives' bytes, then verifies an archive;
#   6. diffs the two builds and requires both identities to be equal;
#   7. inspects one known segment and checks the start of its text.
# Assumes `dotnet restore` has run (scripts/validate.sh does it); needs no network. A CLI that
# does not build is a failure, never a skip. Exits non-zero on any failure.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if [[ -t 1 ]]; then
  RED=$'\033[31m'; GREEN=$'\033[32m'; OFF=$'\033[0m'
else
  RED=""; GREEN=""; OFF=""
fi

FAILED=0
ok() { printf '%sok%s   %s\n' "$GREEN" "$OFF" "$1"; }
fail() { printf '%sFAIL%s %s\n' "$RED" "$OFF" "$1"; FAILED=1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# ------------------------------------------------------------------------ the CLI, once
CLI_DIR="$WORK/cli"
if ! dotnet build src/RulesCorpus.Cli/RulesCorpus.Cli.csproj -c Release --no-restore --nologo \
    -warnaserror -o "$CLI_DIR" >"$WORK/cli-build.log" 2>&1; then
  tail -30 "$WORK/cli-build.log"
  fail "build the rules-corpus CLI"
  exit 1
fi
if [[ ! -f "$CLI_DIR/rules-corpus.dll" ]]; then
  fail "build the rules-corpus CLI (no rules-corpus.dll in the output)"
  exit 1
fi
ok "build the rules-corpus CLI"

cli() { dotnet "$CLI_DIR/rules-corpus.dll" "$@"; }

# expect <exit code> <label> <cli arguments...>: runs the CLI, output to $WORK/last.out/.err.
expect() {
  local want="$1" label="$2"; shift 2
  local got=0
  cli "$@" >"$WORK/last.out" 2>"$WORK/last.err" || got=$?
  if [[ "$got" -eq "$want" ]]; then
    ok "$label (exit $got)"
    return 0
  fi
  grep -hv '^ok ' "$WORK/last.out" "$WORK/last.err" | tail -20 | sed 's/^/     /'
  fail "$label: exit $got, expected $want"
  return 1
}

# Paths from a build definition: the stored sources, or the adapter outputs.
stored_sources() {
  python3 -c 'import json,sys
for s in json.load(open(sys.argv[1], encoding="utf-8"))["sources"]:
    if s.get("stored", True):
        print(s["path"])' "$1"
}
built_outputs() {
  python3 -c 'import json,sys
for d in json.load(open(sys.argv[1], encoding="utf-8"))["derivations"]:
    print(d["output"]["path"])' "$1"
}

# fresh_copy <sample dir> <destination>: the build definition and stored sources only.
fresh_copy() {
  local sample="$1" dest="$2" path
  mkdir -p "$dest"
  cp "$sample/corpus.build.json" "$dest/"
  while IFS= read -r path; do
    mkdir -p "$dest/$(dirname "$path")"
    cp "$sample/$path" "$dest/$path"
  done < <(stored_sources "$sample/corpus.build.json")
}

# same_files <label> <dir a> <dir b> <paths...>: byte-for-byte comparison of each path.
same_files() {
  local label="$1" a="$2" b="$3"; shift 3
  local path bad=0
  for path in "$@"; do
    if ! cmp -s "$a/$path" "$b/$path"; then
      fail "$label: $path differs (or is missing)"
      bad=1
    fi
  done
  [[ "$bad" -eq 0 ]] && ok "$label ($# file(s) byte-identical)"
}

# check_sample <name> <verify exit> <segment id> <segment text prefix>
check_sample() {
  local name="$1" verify_exit="$2" segment="$3" prefix="$4"
  local sample="samples/$name" one="$WORK/$name-1" two="$WORK/$name-2"
  local allow=()
  [[ "$verify_exit" -eq 3 ]] && allow=(--allow-not-verified)

  echo "-- $name"
  if [[ ! -f "$sample/corpus.build.json" ]]; then
    fail "$name: $sample/corpus.build.json is missing"
    return
  fi
  mapfile -t outputs < <(built_outputs "$sample/corpus.build.json")
  local built=(corpus.json "${outputs[@]}")

  fresh_copy "$sample" "$one"
  expect 0 "$name: build a fresh copy" build --dir "$one" || return
  expect "$verify_exit" "$name: verify --rebuild" verify "$one" --rebuild
  if [[ "$verify_exit" -eq 3 ]]; then
    expect 0 "$name: verify --rebuild --allow-not-verified" verify "$one" --rebuild --allow-not-verified
  fi
  same_files "$name: build matches the committed sample" "$one" "$sample" "${built[@]}"
  expect "$verify_exit" "$name: verify the committed sample --rebuild" verify "$sample" --rebuild

  fresh_copy "$sample" "$two"
  expect 0 "$name: build a second fresh copy" build --dir "$two" || return
  same_files "$name: second build matches the first" "$one" "$two" "${built[@]}"

  expect 0 "$name: pack the first build" pack "$WORK/$name-1.tar" --dir "$one" "${allow[@]}"
  expect 0 "$name: pack the second build" pack "$WORK/$name-2.tar" --dir "$two" "${allow[@]}"
  if cmp -s "$WORK/$name-1.tar" "$WORK/$name-2.tar"; then
    ok "$name: the two archives are byte-identical"
  else
    fail "$name: the two archives differ"
  fi
  expect "$verify_exit" "$name: verify the archive" verify "$WORK/$name-1.tar"

  if expect 0 "$name: diff the two builds" diff "$one" "$two" --json; then
    if python3 -c 'import json,sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
sys.exit(0 if d["contentDigestEqual"] and d["manifestDigestEqual"] else 1)' "$WORK/last.out"; then
      ok "$name: contentDigest and manifestDigest are equal"
    else
      fail "$name: the two builds' digests differ"
    fi
  fi

  if expect 0 "$name: inspect segment $segment in the archive" inspect "$segment" --corpus "$WORK/$name-1.tar" --json; then
    if python3 -c 'import json,sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
sys.exit(0 if d["segment"]["id"] == sys.argv[2] and d["text"].startswith(sys.argv[3]) else 1)' \
        "$WORK/last.out" "$segment" "$prefix"; then
      ok "$name: segment $segment starts with the expected text"
    else
      fail "$name: segment $segment does not start with '$prefix'"
    fi
  fi
}

check_sample regulatory 0 "107.9" "§ 107.9 Safety event reporting."
check_sample regulatory-xml 0 "107.2" "<DIV8 N=\"107.2\""
check_sample rulebook 3 "p191.b1" "Unconscious [Condition]"

echo
if [[ "$FAILED" -eq 0 ]]; then
  echo "sample-corpus: PASS"
else
  echo "sample-corpus: FAIL"
fi
exit "$FAILED"
