# Corpus format, schema 1

This is the on-disk contract. A change to anything here is a compatibility event and needs a
record in [decisions/](decisions/).

## A corpus directory

```
<corpus>/
├── corpus.build.json   what to build: sources, derivations, baselines (hand-written)
├── corpus.json         what was built: the manifest (written by `rules-corpus build`)
└── <artifact paths>    every stored artifact, at the relative path its record declares
```

Artifact paths are relative, use `/`, have no empty, `.` or `..` component, are not absolute,
and resolve inside the corpus directory. `corpus.json` and `corpus.build.json` are reserved
and are never artifact paths.

A packed corpus is a tar archive of exactly these files (see [Packing](#packing)).

## Identifiers

| Name | Grammar | Example |
|---|---|---|
| corpus id, artifact id, derivation id, source id | `[a-z0-9]+([-.][a-z0-9]+)*`, at most 128 characters | `cfr-14-107`, `srd-5.2.1` |
| hash derivation | the same grammar (the rules-kernel `SourceBaselineId.HashDerivation` grammar) | `srd-5.2.1-pdftotext-24.02.0-page-marked` |
| segment id | `[A-Za-z0-9]([A-Za-z0-9._()/-]*[A-Za-z0-9)])?`, at most 256 characters | `107.51`, `p13.b2` |
| parameter / metadata key | `[a-z][a-zA-Z0-9]*`, at most 64 characters | `pageMarker` |
| date | `YYYY-MM-DD`, a real calendar date | `2026-01-01` |
| media type | `type/subtype`, lowercase ASCII, at most 128 characters | `text/plain` |

A source id doubles as the rules-kernel `SourceBaselineId.SourceId` and `SourceLocator.SourceId`,
so it uses the stricter corpus grammar rather than the kernel's looser one: every source id
valid here is valid there.

## Digests

A digest is written `sha256:` followed by 64 lowercase hexadecimal digits. SHA-256 is the only
algorithm in schema 1; the prefix exists so the algorithm is explicit in the data rather than
implied. Projected into rules-kernel, the `contentHash` is the hex part alone.

## Canonical JSON

Every digest over JSON is taken over this exact byte form, never over a file as it happens to
be formatted:

- UTF-8, no byte-order mark, no insignificant whitespace.
- Object members sorted by key, comparing keys as sequences of UTF-16 code units (keys are
  ASCII here, so this is also byte order). Duplicate keys are an error when reading.
- Strings: `"` and `\` escaped as `\"` and `\\`; U+0008, U+0009, U+000A, U+000C, U+000D as
  `\b`, `\t`, `\n`, `\f`, `\r`; every other code point below U+0020 as `\u00xx` with lowercase
  hex; everything else written literally as UTF-8. A lone surrogate is an error.
- Numbers: only non-negative integers up to 2^53 − 1, no sign, no leading zeros, no fraction
  or exponent.
- `true` and `false` as written. `null` is never written: an absent optional member is omitted.
- Arrays keep their order. Every array in this format is ordered by construction.

`corpus.json` on disk is the same form with two-space indentation, `": "` after keys, one
member or element per line, and a final `\n`. Readers accept any whitespace; writers produce
exactly this. The writer is the only thing that formats it.

## The manifest: `corpus.json`

```json
{
  "artifacts": [ ... ],
  "baselines": [ ... ],
  "contentDigest": "sha256:...",
  "corpusId": "example",
  "derivations": [ ... ],
  "manifestDigest": "sha256:...",
  "schema": "rules-corpus/manifest/1",
  "segments": [ ... ]
}
```

Unknown members anywhere are an error. Every member below is required unless marked optional.

### `artifacts[]`

One record per artifact: every acquired original and every derived artifact. Order: the
build definition's `sources[]` in order, then adapter outputs in `derivations[]` order.

| Member | Meaning |
|---|---|
| `id` | artifact id, unique in the manifest |
| `role` | `source` (acquired evidence) or `derived` (produced by a derivation) |
| `mediaType` | media type |
| `bytes` | exact length in bytes |
| `digest` | digest of the exact bytes |
| `stored` | `true` when the bytes are in the corpus at `path`; `false` when they are retained elsewhere and only their identity is declared |
| `path` | optional; required when `stored` is `true`, absent otherwise |
| `acquisition` | required for `source`, absent for `derived`: `{ "origin": string, "retrieved": date (optional), "notes": string (optional) }` |
| `derivedBy` | required for `derived`, absent for `source`: the derivation id that produced it |

`origin` is where the bytes came from: a URL, a publication reference, a person. It is recorded
evidence, never fetched. `retrieved` is supplied by whoever acquired the bytes; nothing reads a
clock.

### `derivations[]`

Order: the build definition's `external[]` in order, then its `derivations[]` in order.

| Member | Meaning |
|---|---|
| `id` | derivation id, unique in the manifest |
| `inputs` | non-empty array of artifact ids, each declared earlier in `artifacts` order than `output` |
| `output` | the derived artifact id; exactly one derivation per derived artifact |
| `tool` | `{ "id": id, "version": string }`: the adapter, or the external program |
| `parameters` | object of parameter key to string; `{}` when there are none |
| `reproducibility` | `reproducible` (a rules-corpus adapter re-runs it offline) or `external` (produced outside rules-corpus; verified by digest, re-derivable only with the named tool) |
| `fidelity` | `lossless`, `lossy-traceable` or `non-reversible` |
| `losses` | array of strings saying what the derivation discarded; `[]` exactly when `fidelity` is `lossless` |

These are claims recorded for audit, not semantic judgements.

### `baselines[]`

The projection engines consume, one per cited source, mapping 1:1 onto rules-kernel
`SourceBaselineId`:

| Member | Meaning |
|---|---|
| `sourceId` | source id, unique in the manifest |
| `artifact` | the artifact whose digest is the content hash |
| `hashDerivation` | what the hash covers, in the kernel grammar |
| `asOf` | optional date; absent means the source has no temporal dimension |

`contentHash` is not repeated: it is the named artifact's digest. A baseline may name a source
artifact directly (the acquired bytes are what engines cite) or a derived one.

### `segments[]`

Addressable units of canonical content, in derivation order and, within a derivation, in
document order.

| Member | Meaning |
|---|---|
| `id` | segment id, unique in the manifest |
| `artifact` | the derived artifact the segment is a span of; must be `stored` |
| `start` | byte offset into that artifact |
| `length` | byte length, at least 1; the span lies within the artifact and on UTF-8 boundaries |
| `digest` | digest of exactly those bytes |
| `locator` | optional human-facing locator string, verbatim from the source (`§ 107.51`, `p. 13`) |
| `sources` | optional array of source spans (below) |

A source span maps the segment back to evidence it came from:

```json
{ "artifact": "srd-text", "pages": { "from": 13, "to": 14 } }
{ "artifact": "srd-text", "start": 120, "length": 428 }
```

`artifact` names an artifact in the manifest. A span carries `pages` (1 ≤ `from` ≤ `to`), or
`start` and `length` (a byte range lying within that artifact), or both.

### The two identities

- **`contentDigest`** is the digest of the canonical JSON of
  `{ "baselines": [...], "segments": [...] }`, where each baseline is
  `{ "asOf"?, "contentHash", "hashDerivation", "sourceId" }` (`contentHash` being the named
  artifact's digest) and each segment is `{ "artifact", "digest", "id", "length", "start" }`.
  It changes when the content engines cite or its addressing changes, and only then: not for
  acquisition metadata, tool versions, locators, source spans or paths.
- **`manifestDigest`** is the digest of the canonical JSON of the whole manifest with the
  `manifestDigest` member removed. It changes when anything declared changes.

Content identity and manifest identity are distinct so a metadata edit cannot pass for a
content change, and a content change cannot hide behind a stable label
([decision 0002](decisions/0002-two-identities.md)).

## The build definition: `corpus.build.json`

Hand-written. `rules-corpus build` reads it with the sources on disk and writes every derived
artifact and `corpus.json`.

```json
{
  "schema": "rules-corpus/build/1",
  "corpusId": "example",
  "sources": [
    { "id": "rulebook-pdf", "mediaType": "application/pdf", "stored": false,
      "bytes": 6031375, "digest": "sha256:...",
      "origin": "https://example.org/rulebook.pdf", "retrieved": "2026-09-14" },
    { "id": "rulebook-text", "path": "sources/rulebook.txt", "mediaType": "text/plain" }
  ],
  "derivations": [
    { "id": "rulebook-segmented", "adapter": "text", "input": "rulebook-text",
      "output": { "id": "rulebook-canonical", "path": "canonical/rulebook.txt" },
      "parameters": { "segmentation": "blocks", "pageMarker": "^\\{([0-9]+)\\}$" } }
  ],
  "external": [
    { "id": "rulebook-extraction", "inputs": ["rulebook-pdf"], "output": "rulebook-text",
      "tool": { "id": "pdftotext", "version": "24.02.0" },
      "fidelity": "lossy-traceable", "losses": ["layout", "fonts", "images"] }
  ],
  "baselines": [
    { "sourceId": "rulebook", "artifact": "rulebook-text",
      "hashDerivation": "rulebook-pdftotext-24.02.0-page-marked" }
  ]
}
```

- A `sources[]` entry is either stored (`path`; `bytes` and `digest` are computed and must
  not be given) or not (`"stored": false` with `bytes` and `digest` declared). `stored`
  defaults to `true`. A source with an `external` derivation pointing at it is still a source
  file on disk; the external record says how it was produced and becomes its `derivedBy`.
  Its role in the manifest is then `derived`, and it carries no `acquisition` — the source
  entry's `origin` and `retrieved` must be absent for it.
- `derivations[]` run a rules-corpus adapter (`adapter` names it) over one input. The output's
  `mediaType` is the adapter's. The adapter supplies fidelity, losses and segments.
- `external[]` records derivations performed outside rules-corpus. `reproducibility` is
  `external`; `parameters` is optional.
- `baselines[]` is copied into the manifest as written.

Build refuses to write to a source path, refuses unknown members, and refuses a build
definition whose ids collide.

## Verification

`verify` proves an on-disk or packed corpus matches its manifest, offline. Every check has one
of three outcomes: **ok**, **failed**, or **not verified** (the evidence needed is not
available, such as an artifact with `stored: false` or an `external` derivation under
`--rebuild`). Not verified is never reported as ok.

Checks: schema and grammar of every member; every reference resolves; every stored artifact's
bytes and digest; every segment's bounds, UTF-8 boundaries and digest; every source span's
artifact and range; derivation order and one-derivation-per-derived-artifact; baseline
uniqueness; `contentDigest`; `manifestDigest`. With `--rebuild`, every `reproducible`
derivation is re-run and its output and segments must be byte-identical to the manifest.

## Packing

`rules-corpus pack` verifies first, then writes a POSIX tar (PAX format) containing
`corpus.build.json`, `corpus.json` and every stored artifact, in ordinal path order, each entry
a regular file with mode 0644, uid and gid 0, empty user and group names, and modification time
0. The same corpus always packs to the same bytes. Tar is used rather than zip because
compressed output would depend on the compressor's version.

## Limits

Builds refuse, rather than truncate: any single artifact over 256 MiB, more than 1,000,000
segments in a manifest, and a manifest file over 256 MiB. Adapters receive the limits and
apply their own (see [adapter-contract.md](adapter-contract.md)).
