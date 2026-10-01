# 0012. The 1.0 public surface

**Status:** accepted. Decided under plan milestone M6, 2026-10-01.

## Evidence

- `PublicAPI.Unshipped.txt` listed 241 members of 34 types in the core and 6 in the text
  adapter, none yet reviewed.
- Who uses each core type outside the core: the adapters use only the adapter contract and
  `CorpusLimits`; the CLI, which reaches internals through `InternalsVisibleTo`, uses the rest;
  the tests use all of it. The consumers use the CLI and the files, not the library.
- `CorpusGrammar` (identifier grammar predicates) was public "so an adapter can refuse an id
  the core would reject before it does any work". Neither adapter does: the core validates every
  id an adapter returns, and the text adapter keeps its own copy of the segment-id rule.

## Decision

1. The surface in `PublicAPI.Unshipped.txt`, less `CorpusGrammar`, is the 1.0 surface and is
   promoted to `PublicAPI.Shipped.txt`. Every remaining type is either the adapter contract
   (validated by two adapters), a type a public member returns or takes, or an operation the CLI
   performs through it. The plan's first slice names the core model and its reader/writer as
   the library, so they stay public deliberately.
2. `CorpusGrammar` becomes internal. It was an unvalidated promise; adding it back is a
   non-breaking minor change if an adapter ever needs it, while removing it after 1.0 would
   not be.
3. [docs/compatibility.md](../compatibility.md) states what 1.0 promises: schema 1 for the
   manifest and build definition with any change a new schema id, the digest recipes and
   canonical JSON frozen, adapter versions as behaviour, the CLI's commands, exit codes and
   `--json` members, and the shipped API baseline under semantic versioning.

## Compatibility

Pre-1.0, so nothing breaks: `CorpusGrammar` was never shipped. The manifest schema, digests and
CLI are unchanged by this decision.

## Inference

The programmatic reader and verifier are kept because the plan intends them, not because a
consumer has used them; they are the largest unvalidated part of the surface. Keeping them
costs a promise on 34 types; removing them would remove the only way a .NET consumer could
verify without spawning a process. That is a judgment, and the first consumer to use the
library will test it.

## What would change it

A consumer that needs a type this removes or a member that is missing, which is a 1.x addition;
or evidence that the library is never used outside this repository, which would argue for a
smaller package at 2.0.
