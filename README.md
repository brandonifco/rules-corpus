# rules-corpus

Evidence infrastructure for rules engines. Given retained authoritative source artifacts,
rules-corpus reproducibly produces and verifies a portable, immutable, addressable corpus whose
content identity, derivation history and source mappings are explicit, without containing or
interpreting any domain rules.

**Status:** pre-1.0. Milestones M0–M5 of the
[plan](docs/rules-corpus-design-and-development-plan.docx) are done; M4's identity
calibration against both consumers is [recorded](docs/calibration/2026-09-30-m4-identities.md);
the rest of M4 onward is in the [backlog](docs/backlog.md).

- [Architecture](docs/architecture.md)
- [Corpus format](docs/corpus-format.md)
- [Adapter contract](docs/adapter-contract.md)
- [Decisions](docs/decisions/)
- [Backlog](docs/backlog.md)

## Use

The `rules-corpus` command (`src/RulesCorpus.Cli`) is the whole path from source bytes to a
packed corpus. From a checkout, `dotnet run --project src/RulesCorpus.Cli --` runs it; the
example below is the committed regulatory sample, built from its source.

```bash
rules-corpus init my-corpus --corpus-id cfr-14-107-excerpt
rules-corpus import samples/regulatory/sources/14-cfr-107-excerpt.txt --dir my-corpus \
    --id cfr-14-107-text --media-type text/plain \
    --origin "https://www.ecfr.gov/api/versioner/v1/full/2026-01-01/title-14.xml?part=107"
# declare the derivation and baseline (docs/corpus-format.md); the finished file is
# samples/regulatory/corpus.build.json
rules-corpus build --dir my-corpus
rules-corpus verify my-corpus --rebuild
rules-corpus pack my-corpus.tar --dir my-corpus
rules-corpus inspect 107.9 --corpus my-corpus.tar
rules-corpus diff samples/regulatory my-corpus.tar
```

Every command takes `--json` for machine-readable output on stdout. Exit codes: 0 success;
1 a failed check or refused input; 2 a usage error; 3 some checks were not verified and none
failed. A consumer names the checks it accepts as not verified with
`--expect-not-verified` ([decision 0008](docs/decisions/0008-consumers-pin-the-checks-they-accept-as-not-verified.md));
`--allow-not-verified` accepts any. `rules-corpus --help` has the details.

Three worked examples are committed in [samples/](samples/README.md), with their built outputs:
a regulatory excerpt segmented by section headings, the same part as XML segmented by its
section elements, and a rulebook excerpt whose source PDF is
declared but not stored, so it verifies as not verified. `tools/sample-corpus/check.sh` rebuilds
all three from their sources through the CLI and fails if a single byte differs from what is
committed; the gate runs it.

## Validate

```bash
./scripts/validate.sh full
```
