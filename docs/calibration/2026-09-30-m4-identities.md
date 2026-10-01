# M4 calibration, first half: the consumers' identities (2026-09-30)

Plan milestone M4 calibrates rules-corpus against its two real consumers, the engines
`faa-part-107` and `srd-52-combat` (public repositories brandonifco/faa-part-107 and
brandonifco/srd-52-combat). This record covers the first half: proving that rules-corpus,
given each consumer's committed corpus file, produces a manifest whose baseline projection is
exactly the baseline the consumer already pins in its provenance.json. Neither consumer
repository was modified.

Labels: **Evidence** is something run or read, with its output. **Inference** is a
conclusion drawn from evidence but not itself observed.

## The tooling

- `tools/calibration/consumers.json`: each consumer's pinned commit, the path of its corpus
  file in that repository, its build definition here, and the baseline values copied from the
  `corpus` block of its provenance.json.
- `tools/calibration/faa-part-107.corpus.build.json` and
  `tools/calibration/srd-52-combat.corpus.build.json`: the build definitions.
- `tools/calibration/run.sh <consumer> [--repo <path>] [--corpus-file <path>]`: extracts
  the corpus file at the pinned commit, checks the provenance.json at that commit, checks the
  bytes' SHA-256, builds the CLI from this checkout and the corpus into a fresh temporary
  directory, asserts the baseline projection, reports segments, runs `verify --rebuild`, and
  builds a second copy to compare byte for byte. It is not part of `scripts/validate.sh`
  because it needs the consumers' checkouts. It builds fresh every run and nothing it builds
  is committed, so a change to the manifest format does not leave a stale copy here.

### The two build definitions

- **faa-part-107**: one stored source, `cfr-14-107-xml` (`application/xml`, origin the eCFR
  versioner URL for part 107 as of 2026-01-01), and a baseline directly on it: sourceId
  `cfr-14-107`, hashDerivation `ecfr-versioner-xml`, asOf 2026-01-01. No derivations and no
  segments, because segmenting XML needs an XML adapter (M5).
- **srd-52-combat**: modelled on `samples/rulebook/corpus.build.json`. The PDF `srd-pdf` is
  declared by identity only (6031375 bytes, the same digest as the rulebook sample); the text
  `srd-text` is the output of the external derivation `srd-extraction` (pdftotext 24.02.0),
  whose losses are the sample's minus the excerpt line; the `text` adapter derivation
  `srd-blocks` runs with `segmentation` `blocks`, `pageMarker` `^\{([0-9]+)\}$` and
  `pagesContiguous` `true`. The baseline names `srd-text`: sourceId `srd-5.2.1`,
  hashDerivation `srd-5.2.1-pdftotext-24.02.0-page-marked`, no asOf.

## What was run

Environment (evidence): .NET SDK 10.0.112, Python 3.12.3, git 2.43.0, rules-corpus at
`build/m0-m3` (f7aafd07900bfd040e3ed3e8248e0e84db10b6ff).

Pinned commits (evidence: read from each checkout's `.git/HEAD` and `.git/refs/heads/main` on
2026-09-30; both HEADs are `main`):

| Consumer | Commit | Corpus file |
|---|---|---|
| faa-part-107 | b8fbb6b27025300c58e43eaaaca8ad2f75753e56 | corpus/part107.xml |
| srd-52-combat | 59904e2af7f80887390098a987764727a14e8c77 | corpus/srd-5.2.1.txt |

**The pinned-commit path was not executed in this run.** (It was later the same day; see
[The pinned-commit run](#the-pinned-commit-run).) The session that produced this
record ran in an isolated agent worktree whose sandbox refuses git commands aimed at any
other repository, so `git -C <checkout> rev-parse` and `git show <commit>:<path>` could not
run. The runs below therefore used `--corpus-file` with the checkouts' working-tree files,
which `run.sh` reports as not verified and exits 3 for:

```bash
tools/calibration/run.sh faa-part-107  --corpus-file ~/faa-part-107/corpus/part107.xml
tools/calibration/run.sh srd-52-combat --corpus-file ~/srd-52-combat/corpus/srd-5.2.1.txt
```

Inference: the working-tree files are the files at the pinned commits. Their SHA-256 equals
the contentHash each provenance.json records, and each provenance.json lists the same digest
for the corpus path among its `generated` files, but a dirty working tree with the same
bytes cannot be told apart this way. The command that closes this is
`tools/calibration/run.sh <consumer>` without `--corpus-file`, from a shell that can run git
against the checkouts; it also checks the provenance.json at the pinned commit.

## Results

Evidence, from the two runs above (every other check ok; exit 3 only for the two
not-verified pinned-commit lines):

| | faa-part-107 | srd-52-combat |
|---|---|---|
| SHA-256 of the corpus bytes = pinned contentHash | ok (80f6bc4b…ce35e, 79880 bytes) | ok (c55926cb…b100, 1413945 bytes) |
| baseline sourceId | ok, `cfr-14-107` | ok, `srd-5.2.1` |
| baseline contentHash (named artifact's digest hex) | ok | ok |
| baseline hashDerivation | ok, `ecfr-versioner-xml` | ok, `srd-5.2.1-pdftotext-24.02.0-page-marked` |
| baseline asOf | ok, 2026-01-01 | ok, absent = null |
| segments | 0 (pinned 0) | 18373, `p1.b1` to `p364.b45`, on pages 1-364 |
| `verify --rebuild` | exit 0, 12 checks ok | exit 3: 15 ok, 2 not verified, 0 failed |
| `verify --rebuild --allow-not-verified` | n/a | exit 0 |
| second fresh build byte-identical | ok (3 files) | ok (4 files) |
| build time | ~120 ms | ~1.06 s |
| `verify --rebuild` time | ~120 ms | ~1.07 s |
| contentDigest | sha256:19052fa80e5e7ace3e4665a50d0576c1f93806f89e0e9f226de36720b54a0f0e | sha256:fe383c8933711827e8fa6b6154d71e172a1ac52a54b5e9296a3f9d1858722954 |

The two not-verified checks for srd-52-combat are exactly the ones decision 0004 predicts:
`artifact srd-pdf` (declared by identity only) and `rebuild srd-extraction` (external
derivation by pdftotext 24.02.0). The manifestDigest is not recorded as a result: it covers
the build definitions' notes and will change when the manifest gains members, and nothing
here pins it. Wall time for a whole `run.sh srd-52-combat`, including an incremental CLI
build, was about 6 s.

## Findings

1. **The text adapter accepts the full SRD text under the strict parameters (evidence).** No
   refusal: 364 marker lines `{1}` to `{364}`, contiguous from 1; every page has at least one
   block (fewest 4, on page 4; most 284, on pages 26 and 41; median 34); no block crosses a
   page; segment lengths 1 to 4472 bytes. The input is LF-only UTF-8 without a byte-order
   mark, so the derivation is lossless and the canonical text is byte-identical to the
   source text.
2. **Both baselines project exactly (evidence, subject to the pinned-commit caveat above).**
   The format's baseline shape (a source artifact named directly for faa-part-107, an
   external derivation's output for srd-52-combat) expressed both consumers' existing
   identities without any change to rules-corpus.
3. **The unstored PDF and the external derivation hold outside rules-corpus (evidence).**
   `sha256sum` of rules-factory's `examples/srd-52-combat/SRD_CC_v5.2.1.pdf` gives the declared
   digest, 6031375 bytes, and rules-factory's `extract.py --check` (pdftotext 24.02.0 is
   installed on this machine) re-derives the committed text byte for byte in about 9 s.
   rules-corpus still reports both as not verified, correctly: it neither holds the PDF nor
   runs pdftotext.
4. **The backlog names the wrong function for the consumers (evidence).** Both engines'
   vendored intake.py (under their scripts/factory/) check the digest in `verify_corpus`,
   through a `HASH_DERIVATIONS` table whose three entries are all plain SHA-256 of the file.
   `verify_declared_corpus_digest` exists only in rules-factory's current intake, not in
   either engine; the two engines' intake.py files also differ from each other. Inference:
   the engines' copies predate the factory's rename, which is the drift the backlog names.

## The pinned-commit run

This closes the caveat under [What was run](#what-was-run) (issue #2).

Environment (evidence): .NET SDK 10.0.112, Python 3.12.3, git 2.43.0, rules-corpus at `main`
(f87c4d32f22204a17ad4d9ea140a7ecff32f4db3), on 2026-09-30, from a shell that can run git
against both checkouts. The faa-part-107 checkout's HEAD had moved on to
684cb7d18a4f1e6124970dc40a3e6620e0eff855; `run.sh` reads the pinned commit regardless. The
srd-52-combat checkout was a fresh clone whose HEAD is the pinned commit.

```bash
tools/calibration/run.sh faa-part-107
tools/calibration/run.sh srd-52-combat
```

Evidence: both runs print `PASS` and exit 0. Every step-1 check is `ok`: the pinned commit
exists, the corpus file is extracted at it (79880 and 1413945 bytes), and the provenance.json
at it records the pinned baseline values. Everything else matches the table under
[Results](#results): the same SHA-256, baseline projection, segment counts and
contentDigests; `verify --rebuild` exits 0 for faa-part-107 and 3 for srd-52-combat (the same
two not-verified checks; 0 under `--allow-not-verified`); the second build is byte-identical.
The manifestDigests differ from any earlier run's only through the build definitions' notes,
and nothing pins them. Wall time for a whole `run.sh`, including an up-to-date CLI build,
was about 1.4 s for faa-part-107 and about 5.6 s for srd-52-combat.

The inference in [What was run](#what-was-run), that the working-tree files were the files at
the pinned commits, is now evidence: bytes extracted with `git show <commit>:<path>` give the
same results.

## What M4 still needs

Done since: [the second half](2026-10-01-m4-migration.md). Each engine's vendored digest check is
replaced by `rules-corpus verify` over a corpus built at intake from these definitions, and
srd-52-combat's two not-verified checks are declared exactly (decision 0008). Segmenting
faa-part-107 still needs an XML adapter (M5).
