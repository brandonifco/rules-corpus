# 0009. A segment's source spans name its derivation's input

**Status:** accepted. Decided under the review follow-up of 2026-10-01.

## Evidence

A resealed manifest whose first segment's source span named the segment's own derived artifact
instead of the derivation's input parsed without error: `CheckSourceSpans` only range-checked
a span against whatever artifact it named. Verification without `--rebuild` parses the
manifest and checks bytes, so it would pass too. The builder always writes the input's id
(`AdapterRun.CheckSpan`), and the adapter contract defines source spans as spans "into the
input" (`docs/adapter-contract.md`), so the manifest already meant this; nothing enforced it.

## Decision

Manifest validation requires that, for a segment of an artifact produced by a reproducible
derivation with exactly one input, every source span's `artifact` is that input. The error is
reported at `$.segments[i].sources[k].artifact`. A span naming an undeclared artifact keeps
its existing error and is not reported twice. Segments of artifacts with an external
derivation are not constrained: no adapter wrote their spans.

## Compatibility

No schema, serialized form or public type changes. A manifest this build wrote still reads;
a hand-edited or foreign manifest whose spans name another artifact is now refused at parse.

## Inference

Binding spans to the input is sufficient: a reproducible derivation has exactly one input,
so the check does not need `--rebuild`. Whether the byte range is within that input is still
checked by the existing range check against the named artifact.

## What would change it

A derivation with several inputs whose adapter maps segments to more than one of them. None
exists, and the validator already requires exactly one input.
