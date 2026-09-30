# 0005. Canonical JSON is produced by our own writer

**Status:** accepted.

## Decision

Digest inputs and `corpus.json` are produced by a small writer in `RulesCorpus` that
implements the rules in docs/corpus-format.md exactly. `System.Text.Json` is used to *read*
(through `Utf8JsonReader`, rejecting duplicates and unknown members), never to produce bytes
that are hashed.

## Why

A digest over JSON is only reproducible if the byte form is specified. `System.Text.Json`'s
escaping is controlled by its encoder, whose defaults escape non-ASCII and HTML-sensitive
characters and are not a published contract across runtime versions (inference: we have not
observed a change, but nothing promises there won't be one, and the library multi-targets
net8.0 and net10.0). A hundred lines we own and test against fixed vectors is cheaper than
that uncertainty.
