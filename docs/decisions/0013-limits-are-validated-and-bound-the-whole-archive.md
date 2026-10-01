# 0013. Limits are validated where set and bound the whole archive

**Status:** accepted. Decided in the post-1.0 hardening pass, 2026-10-01.

## Evidence

- `CorpusLimits` accepted any value. A negative or zero bound refused every input with a
  misleading message, and a `MaxArtifactBytes` above `Array.MaxLength` (about 2 GiB) reached
  `new byte[length]` in the file readers, where a file larger than one array can hold would fail
  as an out-of-memory error rather than a refusal.
- `PackedCorpusFiles.Load` bounded each entry but not the entry count or the total, and held
  every entry in memory before any manifest check ran. An archive of many entries each just under
  the per-entry bound exhausted memory. Read from the code path: the only check was
  `entry.Length > max(MaxArtifactBytes, MaxManifestBytes)` per entry.

## Decision

1. Each `CorpusLimits` property validates in its `init` accessor and throws
   `ArgumentOutOfRangeException` naming the property: at least 1 for every bound, and at most
   `Array.MaxLength` for `MaxArtifactBytes` and `MaxManifestBytes`.
2. Two properties are added, `MaxPackedEntries` (default 10,000) and `MaxPackedBytes` (default
   1 GiB), so the aggregate bound flows through the existing limits model. A packed corpus over
   either is refused with the count or total and the limit, and nothing already read is used.

The defaults are rationalized in the property documentation: 10,000 entries because every stored
artifact is declared and cited individually and real corpora hold a handful; 1 GiB because it is
four times the default per-file bound, enough for the manifest, the build definition and several
maximum-size artifacts. A caller that needs more raises the limit.

## Compatibility

Additive public API: two properties on `CorpusLimits`, declared in `PublicAPI.Unshipped.txt`.
By docs/compatibility.md a release that adds API is a minor release, so this ships as 1.1.0, not
1.0.1; `VersionPrefix` is not changed by this record. No schema, digest, canonical JSON, CLI or
packed-format change. Code that set a now-invalid value (zero, negative, over an array) was
already unable to work and now fails earlier and clearly. Normal corpora are unaffected: defaults
are far above any corpus this repository holds.

## Inference

The per-entry and aggregate bounds are different protections and both stay. Refusing at the
setter, rather than clamping, follows the repository's rule that a bound is refused, not
truncated.

## What would change it

A consumer whose real corpora exceed the defaults, which raises them; or a streaming reader that
no longer holds entries in memory, which would make the aggregate bound unnecessary.
