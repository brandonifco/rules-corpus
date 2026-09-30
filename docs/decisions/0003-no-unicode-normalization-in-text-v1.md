# 0003. The text adapter does not normalize Unicode

**Status:** accepted.

## Evidence

On SDK 10.0.112 with `InvariantGlobalization` true (this repository's setting),
`"é".Normalize(NormalizationForm.FormC)` returned its input unchanged and
`IsNormalized()` returned `true`. Normalization is silently a no-op, and whether it happens
depends on the host application's globalization mode, not on this library.

No current derivation normalizes Unicode (rules-factory's `HASH_DERIVATIONS` all hash raw
bytes, and quotes are held to the raw extraction).

## Decision

Text v1 offers no Unicode normalization parameter. Recording "NFC applied" when it might not
have been would be a false derivation record.

## What would change it

A consumer that needs it, and an implementation whose output does not depend on the host
(pinned tables, tested against the Unicode conformance file).
