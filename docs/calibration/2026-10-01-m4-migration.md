# M4 calibration, second half: the consumers verify through rules-corpus (2026-10-01)

The [first half](2026-09-30-m4-identities.md) showed that rules-corpus, given each consumer's
committed corpus file, reproduces the baseline the consumer pins. This record covers the second
half (issue #4): each engine's own digest recipe is gone, and both engines' gates verify their
corpus by running rules-corpus through rules-factory's normal production path.

Labels: **Evidence** is something run or read, with its output. **Inference** is a
conclusion drawn from evidence but not itself observed.

## What landed, and where

| Repository | Change | Merged as |
|---|---|---|
| rules-corpus | `--expect-not-verified` (decision 0008) | #12, `09602e1` |
| rules-factory | rules-corpus builds and verifies every corpus; `HASH_DERIVATIONS` removed (decision 0074) | #559, `a409cf5` |
| rules-factory | `RulesFactory.Maps.FaaPart107` 5.0.0 and `RulesFactory.Maps.Srd52Combat` 3.0.0, the first versions with a verification record (0048) | #560 `0d8fecf`, #569 `d992dd7` |
| rules-factory | the rails read a change list past GitHub's 100-file page; a deleted file spelled `DELETED` is a deletion; publish-map's gate timeout | #565 `8b0eec6`, #567 `5cce501`, #564 `aa57e9d` |
| faa-part-107 | produced from rules-factory `5cce501` with map 5.0.0, plus the edits that forces | #141, `50d923a` |
| srd-52-combat | produced from rules-factory `d992dd7` with map 3.0.0, plus the edits that forces | #76, `6a83195` |

The rules-factory rows are the M4 change itself. The map republishes were forced by it: the
current factory refuses a map package with no verification record, and both engines were
pinned to one. The rails fixes were forced by the size of the engines' produce diffs (139 and
144 files).

## The three open questions, answered

Issue #4 asked these first. The answers are rules-factory decision 0074, and rules-corpus
decision 0008.

- **How a corpus reaches intake.** It is built at intake. The inputs are the committed corpus
  file and two companions committed beside it:
  - `<stem>.corpus.build.json`, the rules-corpus build definition;
  - `<stem>.corpus.expect.json`, `{"expectNotVerified": [...]}`.

  No committed built corpus, and no packed `.tar`, is authoritative; nothing of the kind is
  committed.
- **How intake gets the tool.** `dotnet run` against a pinned checkout of this repository. There
  is no NuGet feed and no `dotnet tool`.
  - The pin is `REPOSITORY` and `COMMIT` in `rules-factory/tools/factory/rulescorpus.py`, and
    nowhere else. It is `09602e1dded1cb9625c6ce0fbfbea0853cbf49c7`.
  - The checkout lives in `~/.cache/rules-factory/rules-corpus/<commit>`, under a file lock.
  - It is built once, then run with `dotnet run --no-build`.
  - Before every use it is refused unless HEAD is the pin and no tracked file is modified.
- **Exit 3 for srd-52-combat.** Each consumer declares the exact set of checks it accepts as
  not verified, and `verify` passes only on an exact match. There is no blanket
  `--allow-not-verified` anywhere on the path.
  - faa-part-107 expects `[]`.
  - srd-52-combat expects `artifact srd-pdf` and `rebuild srd-extraction`.

## What disappeared

Evidence: read from the commits named, with Python's `ast` for the line counts.

| Where | Before | After |
|---|---|---|
| `faa-part-107/scripts/factory/intake.py` (vendored) | `HASH_DERIVATIONS` (10 lines), `verify_corpus` (51 lines) | gone |
| `srd-52-combat/scripts/factory/intake.py` (vendored) | `HASH_DERIVATIONS` (10 lines), `verify_corpus` (62 lines) | gone |
| both engines' `<engine>/scripts/engine-gate.py` | `derivations()` (18 lines), which loaded intake's table for the posture step to recompute through | gone; the posture step calls `intake.verify_declared_corpus` |
| `rules-factory/tools/factory/intake.py` | `HASH_DERIVATIONS` (44 lines, with its comments) and `verify_declared_corpus_digest` (16 lines) | `ADMITTED_HASH_DERIVATIONS`, a set of names with no functions, and `verify_declared_corpus` (37 lines), which calls rules-corpus |
| `rules-factory/tools/factory/provenance.py` | its recompute looked up `intake.HASH_DERIVATIONS` | it re-produces, which verifies through rules-corpus |

The drift that finding 4 of the first half reported is gone too:

| | before (faa `b8fbb6b` / srd `59904e2`) | after (faa `50d923a` / srd `6a83195`) | rules-factory `d992dd7` |
|---|---|---|---|
| `<engine>/scripts/factory/intake.py` | `bc00d43b…` / `002730b8…` | `df923af4…` / `df923af4…` | `df923af4…` |
| `<engine>/scripts/factory/rulescorpus.py` | absent | `b4e40298…` / `b4e40298…` | `b4e40298…` |
| `<engine>/scripts/engine-gate.py` | different | `40076ad7…` / `40076ad7…` | `40076ad7…` |

Each cell is the SHA-256 of the file's bytes. rules-factory's own copies are its `rules-factory/tools/factory/intake.py`, `rules-factory/tools/factory/rulescorpus.py` and `rules-factory/tools/factory/recipe/engine-gate.py`, which produce writes into each engine.

Every function the three old tables held was SHA-256 of the whole file. No engine, and no
part of rules-factory, now turns corpus bytes into a baseline. The SHA-256 that rules-factory
still computes is of package members and generated files (provenance integrity), not of a
corpus baseline. rules-factory's gate test asserts that no `*DERIVATIONS = {` table and no
`hashlib.sha256(data)` recipe remain in the vendored gate or intake.

## What each side owns

**rules-corpus owns** turning bytes into an identity:

- the build: sources, derivations, canonical text, segments, the manifest;
- every digest, including the baseline's `contentHash`;
- `verify --rebuild`: the classification of each check as ok, not verified or failed;
- the exact-match rule for `--expect-not-verified`;
- the exit codes.

**rules-factory owns** everything about *which* corpus, and *whether to admit it*:

- the pin of rules-corpus and the checkout it runs;
- the two companion files' place beside the corpus file, and copying them with the stored
  sources into a fresh temporary directory per build;
- `ADMITTED_HASH_DERIVATIONS`, the names a map may cite: admission policy, not a recipe;
- comparing the built baseline with the map package's declaration: `sourceId`,
  `hashDerivation`, `contentHash`, `asOf`, and that the supplied file is the artifact the
  baseline names;
- the posture: `committed-copy` or `local-copy`;
- carrying the companions into each engine's `corpus/` at produce, and hashing them in
  `provenance.json`;
- an in-process memo of verified builds. Its key is the pin, the cache root and every input
  byte. A refusal is never memoized.

## How each consumer invokes it

The call path is the same for both engines, and in rules-factory's own intake, `pack-map` and
provenance recompute:

```
scripts/validate.sh full
  → scripts/engine-gate.py posture --manifest <package corpus-manifest.json> --map <merged map> --name <Engine>
    → scripts/factory/intake.py  verify_declared_corpus(sourceId, manifest corpus, corpus/<file>)
      → scripts/factory/rulescorpus.py  build_and_verify(corpus/<file>)
        → dotnet run --no-build --project src/RulesCorpus.Cli/RulesCorpus.Cli.csproj -c Release -- build --dir <tmp> --json
        → dotnet run --no-build --project src/RulesCorpus.Cli/RulesCorpus.Cli.csproj -c Release -- verify <tmp> --rebuild [--expect-not-verified <names>] --json
```

The CLI runs in the pinned checkout, under that checkout's `global.json`. When the expectation
is `[]` the flag is omitted, so any not-verified check exits 3 and is refused. faa-part-107
runs without the flag; srd-52-combat runs with
`--expect-not-verified "artifact srd-pdf,rebuild srd-extraction"`.

What each gate printed (evidence: `./scripts/validate.sh full` in each engine):

```
faa-part-107 @ 50d923a:  verified: cfr-14-107 (committed-copy, pin-in-repo): committed at corpus/part107.xml builds with rules-corpus 09602e1dded1 to the pinned baseline
srd-52-combat @ 6a83195:  verified: srd-5.2.1 (committed-copy, pin-in-repo): committed at corpus/srd-5.2.1.txt builds with rules-corpus 09602e1dded1 to the pinned baseline
```

## Exit behaviour

| rules-corpus | rulescorpus.py | intake / gate |
|---|---|---|
| `build` exit ≠ 0 | `Refused`, with the tool's report | the posture step lists a problem, and the gate FAILs |
| `verify` exit 0 | the manifest's baselines are read | the baseline is compared with the map package's, and any difference FAILs |
| `verify` exit 1: a check failed, or the expectation is not met exactly | `Refused` (`NOT VERIFIED -- … exited 1`) | FAIL |
| `verify` exit 3: not verified, no expectation flag | `Refused` | FAIL |
| exit 2 (usage), any other exit, or JSON missing on exit 0 | `Unavailable` | `Refused`, then FAIL |
| cannot fetch, build, or match the pin | `Unavailable` | `Refused`, then FAIL; nothing falls back |

The gate's own exit 3 (NOT VERIFIED) is reserved for a `local-copy` corpus whose environment
variable is unset. Neither consumer has one: both are `committed-copy`.

## The demonstrations

1. **The factory path.** Both engines were produced by `factory produce` from a clean
   rules-factory checkout, and the produce's own gate verified the corpus through rules-corpus.
   Evidence, from the produce output:
   - faa-part-107: `corpus cfr-14-107: ecfr-versioner-xml 80f6bc4b… built and verified by
     rules-corpus 09602e1dded1`; 770 tests ×2 frameworks; `validate.sh full: PASS`.
   - srd-52-combat: `corpus srd-5.2.1: srd-5.2.1-pdftotext-24.02.0-page-marked c55926cb… built
     and verified by rules-corpus 09602e1dded1`; 272, then 292, tests ×2; `validate.sh full: PASS`.
   - `factory provenance --engine` recomputes each record and reports `every field matches`.
2. **Pinned commits.** Each engine's `provenance.json` names the factory commit it was produced
   from (`5cce501`, `d992dd7`). The vendored `rulescorpus.py` names rules-corpus `09602e1`.
   rules-factory's tests refuse a checkout at another commit and a checkout with an edited
   tracked file: `test_a_checkout_that_is_not_the_pinned_commit_is_refused` and
   `test_a_checkout_with_an_edited_tracked_file_is_refused`.
3. **Changed corpus, changed expectation, unexpected not-verified.** Evidence: srd-52-combat's
   real corpus and companions were copied to a temporary directory and altered one way each.
   Each copy then went through the production call, `intake.verify_declared_corpus`:

   | Input | Result |
   |---|---|
   | as committed | verified |
   | one corpus byte changed | refused: rules-corpus gives `58967a1a…` under `srd-5.2.1-pdftotext-24.02.0-page-marked`, and the manifest declares `c55926cb…` |
   | expectation `[]` | refused: `verify` exited 3, with `artifact srd-pdf` and `rebuild srd-extraction` not verified |
   | expectation missing `rebuild srd-extraction` | refused: exited 1, `not verified but not expected: rebuild srd-extraction` |
   | expectation naming an extra check (`artifact srd-text`) | refused: exited 1, `expected not verified but not reported so: artifact srd-text` |
   | expectation file absent | refused before anything runs: every corpus must declare its expectation, and `[]` accepts none |

4. **A failed verification cannot be rescued.** Evidence, at rules-corpus `09602e1`:
   - Build srd-52-combat's corpus, then append one byte to `canonical/srd-5.2.1.txt`.
   - `verify --rebuild` under the committed expectation exits 1: `failed (18 checks: 14 ok,
     2 not verified, 2 failed)`, with `artifact srd-canonical` and `segments srd-canonical`
     failed.
   - The same, with both failed checks also named in `--expect-not-verified`, still exits 1:
     `expected but not reported not verified: artifact srd-canonical, segments srd-canonical`.
     A failed check is never not-verified, so no expectation can match it.
5. **No fallback.** `test_a_rules_corpus_that_cannot_be_fetched_verifies_nothing` points the
   repository at a path that does not exist. The intake is refused, and nothing is verified
   by any other means. Its recorded mutation, a local SHA-256 fallback, turns it red.
6. **Determinism.**
   - `verify --rebuild` re-derives the canonical text and segments and compares them byte for
     byte. srd-52-combat prints: `rebuild srd-blocks: adapter text 1 re-derived byte-identical
     output and 18373 identical segment(s)`.
   - Repeated runs give the same baseline: two timed runs per engine below, the produce, and
     the re-produce.
   - A re-produce of srd-52-combat after an overlay edit changed exactly one file,
     `provenance.json`.
   - The memo is not a source of nondeterminism: its key covers every input byte, and
     `test_a_changed_corpus_byte_is_built_again` and `test_a_changed_expectation_is_verified_again`
     go red when the key leaves either out.

## Cost

Evidence: one `verify_declared_corpus` call, the memo cleared, with a warm and built checkout.
The full production path, `build` then `verify --rebuild`, ran twice per engine:

| | faa-part-107 | srd-52-combat |
|---|---|---|
| per corpus | 0.77 s, 0.75 s | 2.81 s, 2.76 s |

The first use on a machine also clones rules-corpus at the pin and builds it. That needs the
network, which the gate already needed to restore packages. rules-factory's own test suite
verifies many corpora repeatedly; the memo brought its local run from 7 min 20 s to 3 min 29 s,
and CI's timeout was raised from 10 to 20 minutes.

## Remaining duplication

1. **The vendored copies.** Each engine carries rules-factory's `intake.py`, `rulescorpus.py` and
   `engine-gate.py`. These are byte-identical managed copies: written by produce, and hashed in
   `provenance.json`, so a hand edit fails the gate. They are not independent recipes. They can
   lag the factory until an engine is re-produced, but they can no longer disagree about a
   digest, because none of them computes one.
2. **The build definitions** exist in three places:
   - rules-factory `examples/<engine>/` (the source);
   - each engine's `corpus/`, which is identical to the source;
   - this repository's `tools/calibration/`, which is the same build except for the notes and
     each stored source's path.

   Inference: the calibration copies are now historical, since the engines' own definitions
   are what their gates build. `tools/calibration/run.sh` still runs against the consumers'
   pre-migration commits in `consumers.json`.
3. **`ADMITTED_HASH_DERIVATIONS`** repeats, as names, the `hashDerivation` values the build
   definitions declare. It is admission policy: a name not in the set is refused before
   rules-corpus runs.
4. **The corpus file's SHA-256 in `provenance.json`.** Each engine's record hashes
   `corpus/<file>` among its generated files, as it hashes every generated file. For both
   consumers that hash equals the baseline's `contentHash`, because both baselines name the
   committed bytes directly. It is an integrity check on a generated file, not a recipe.
   Inference: a corpus whose baseline names a derived artifact would make the two values
   differ, and nothing would be wrong.

## What M4 still needs

Issue #4's done-when holds: neither engine carries its own digest recipe for the corpus, and
both gates pass using `rules-corpus verify` through rules-factory's production path. Nothing
open blocks it. These are open and recorded elsewhere:

- rules-factory#561: produce into a git worktree verifies, then refuses to commit, so both
  engines were produced into full clones.
- rules-factory#568: the delta review packet finds the entry under review only through markers
  in the pull request.
- rules-factory#570: generated `Rulings.g.cs` still names `corpus-map.overlay.json`.
- Segmenting faa-part-107 needs an XML adapter (M5). Its corpus still has no segments.
