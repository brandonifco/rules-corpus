# 0009. Diff reports derivations

**Status:** accepted. Decided under the review follow-up of 2026-10-01.

## Evidence

`ManifestDiff.Compare` compared artifacts, baselines and segments only. A manifest whose
derivation changed fidelity from `lossless` to `lossy-traceable` and gained a loss, with
every artifact and segment unchanged, produced `ManifestDigestEqual == false` and empty lists
in every category (reproduced in `DiffTests` before the change). The CLI's human output for
that case said the difference was "elsewhere in the manifest (corpus id, derivations or build
definition)". Principle 3 of CLAUDE.md makes what a derivation discards explicit; a diff that
cannot show a change to it hides exactly that.

## Decision

`ManifestDiff` gains `DerivationsAdded`, `DerivationsRemoved` and `DerivationsChanged`,
matched by derivation id with the same rules as the other records: a change is any difference
in the record's canonical JSON (inputs, output, tool, parameters, reproducibility, fidelity,
losses), added and changed are in the second manifest's order, removed in the first's.
`rules-corpus diff` renders them (`+`/`-`/`~ derivation <id>`) and adds a `derivations`
member, shaped like `artifacts`, to its `--json` result.

## Compatibility

Public surface: three new properties, declared in `PublicAPI.Unshipped.txt`. The CLI's JSON
result gains one member; nothing is removed or reordered. No manifest schema or digest
changes.

## Inference

The one remaining case the human output calls "elsewhere in the manifest" is a corpus id or
build digest change, which has no per-record list.

## What would change it

A consumer that needs a field-level diff of a record rather than before and after records.
No consumer asks for one.
