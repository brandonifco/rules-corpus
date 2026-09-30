# Operating contract

Operating contract for this repository. Read [README.md](README.md) for what rules-corpus is
and [docs/architecture.md](docs/architecture.md) for how it is put together. The founding
plan is [docs/rules-corpus-design-and-development-plan.docx](docs/rules-corpus-design-and-development-plan.docx).

`AGENTS.md` is a symbolic link to this file.

## What this repository is

rules-corpus turns authoritative source material into immutable, addressable, versioned
corpus artifacts that rules engines and rules-factory consume. It proves what source was
used and how it became machine-consumable. It knows nothing about what the rules mean.

## Governing principles

**1. Bytes before meaning.** If a proposed feature requires understanding what a source
passage means, it does not belong here. No domain vocabulary in `src/`: no sections of a
regulation, no spells, no aircraft. `tools/repo-checks.py --only neutrality` catches the
known consumers' vocabulary; it cannot catch the rest, so this is also a review obligation.

**2. Determinism before convenience.** Same source bytes plus the same declared derivation
produce the same canonical bytes, the same segments and the same identities, or the build
fails. Nothing in `src/` reads a clock, randomness, the environment or the network.

**3. Originals are immutable; loss is explicit.** An acquired artifact is never modified.
A derivation that discards information records what it discarded.

**4. Enforcement over prose.** A rule worth stating is worth a check in
`tools/repo-checks.py` or a test. A check whose inputs do not exist reports `skip`, and a
skip fails the gate.

**5. Compatibility changes are decisions.** Changing the manifest schema, the canonical
JSON form, a digest recipe or a public type's shape needs a record in `docs/decisions/`.
`PublicAPI.Unshipped.txt` makes a public-surface change fail the build until it is
declared.

**6. No speculative surface.** No new package, adapter, abstraction or manifest field
without a named current consumer or a release-blocking invariant. Prefer deleting
speculative code to documenting it. New ideas go to [docs/backlog.md](docs/backlog.md).

## Before you change anything

Run the gate. There is one:

```bash
./scripts/validate.sh full
```

## Working rules

- **Smallest coherent change.** One concern per change.
- **Tests are the evidence.** Byte-identical rebuilds, digest values and ordering are
  asserted directly, not inferred.
- **Ordered by construction.** Segments are in document order; never sort a result
  afterwards to make it look stable.
- **Public API carries XML docs.** Say why, not what.
- **Evidence, inference, recommendation.** Design records say which is which.

## Adding a project

Create it, declare it in `ALLOWED_PROJECT_REFS` in `tools/repo-checks.py`, and add it to
`RulesCorpus.slnx`. An undeclared project fails the layering check.
