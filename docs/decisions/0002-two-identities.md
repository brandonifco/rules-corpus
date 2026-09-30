# 0002. Content identity and manifest identity are distinct

**Status:** accepted.

## Decision

`contentDigest` covers the baselines (source id, content hash, hash derivation, as-of) and the
segment table (id, artifact, start, length, digest). `manifestDigest` covers everything else
as well. Both are SHA-256 over canonical JSON (docs/corpus-format.md).

## Why

The plan (§9) recommends it: a metadata edit (a corrected origin URL, a tool's patch version,
a locator's wording) must not look like a content change to an engine, and a content change
must not hide behind a stable label. The corpus id is deliberately outside content identity
for the second reason.

## Inference

Consumers today pin one content hash per source and nothing at corpus level. Whether a
corpus-level content digest earns its place is for the M4 calibration to show; if neither
consumer uses it, it goes in the M6 surface review.
