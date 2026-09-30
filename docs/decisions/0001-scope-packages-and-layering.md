# 0001. Scope, packages and layering

**Status:** accepted. Brandon approved the M0 architecture on 2026-09-30.

## Decision

- `RulesCorpus` (net8.0; net10.0): identifiers, digests, canonical JSON, the manifest and
  build-definition models, the adapter contract, the builder and the verifier. BCL only.
- `RulesCorpus.Adapters.Text` (net8.0; net10.0): the one adapter M2 needs. References only
  `RulesCorpus`.
- `RulesCorpus.Cli` (net10.0, a .NET tool named `rules-corpus`): references both.
- Nothing references rules-kernel, and rules-kernel references nothing here. The manifest's
  `baselines[]` uses the kernel's grammar so the projection onto `SourceBaselineId` is a
  field-for-field copy made by the consumer.

## Evidence

- The plan (§5) asks for independently consumable packages and a format-neutral core.
- rules-kernel's CLAUDE.md excludes "corpus extraction and adapters … anything that reads a
  file" from the kernel, and the kernel is at 1.0: a dependency in either direction would tie
  their release cycles for a projection that is three strings and a date.
- `SourceBaselineId` is `(SourceId, ContentHash, HashDerivation, AsOf?)`; HashDerivation's
  grammar is `[a-z0-9]+([-.][a-z0-9]+)*`. Both consumers' engines construct it from exactly
  those four values today (`MapEntries.g.cs`).

## Not decided here

Whether citation resolution (section designations, heading paths, page-marker walks),
duplicated today across rules-factory's checkers, belongs here. It encodes domain citation
grammar, so by the plan's boundary rule it probably does not. It is in the backlog.
