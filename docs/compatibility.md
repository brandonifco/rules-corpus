# Compatibility contract, 1.0

What a consumer may rely on from 1.0, and what changing it costs. The details of each item
are in [corpus-format.md](corpus-format.md), [adapter-contract.md](adapter-contract.md) and the
[decisions](decisions/); this page says which parts are promises.

A consumer's real dependencies today are the corpus files and the command line: the engines
build and verify a corpus by running `rules-corpus` and read `corpus.json` directly. The
.NET library is a promise too, but a smaller one than the files.

## The corpus format

| Promise | A change to it needs |
|---|---|
| `corpus.json` is `schema` `rules-corpus/manifest/1`, with exactly the members corpus-format.md lists. A reader rejects an unknown member, so even an added optional member is a new schema. | a new schema id (`rules-corpus/manifest/2`) and a decision |
| `corpus.build.json` is `schema` `rules-corpus/build/1`, under the same rule. | a new schema id and a decision |
| Canonical JSON (key order, number and string forms, the indented writer form `verify` demands) and the three digest recipes: `contentDigest`, `manifestDigest`, `buildDigest`. | a new schema id and a decision; an identity must never change meaning under one schema |
| `sha256:` digests, and the grammars of ids, segment ids, media types, dates and hash derivations. | a new schema id and a decision |
| `pack` writes a deterministic uncompressed PAX tar (decision 0006); the same corpus packs to the same bytes. | a decision |
| Verification binds every shipped byte (decision 0007); a corpus that verifies `ok` verifies on any machine, offline. | a decision |
| An adapter's id and `Version` name a behaviour: `text` 1 and `xml` 1 emit the same bytes and segments for the same input and parameters forever. | a new `Version` of that adapter |

A 1.x release never changes what an existing corpus means. It may add a schema alongside 1,
and read both.

## The command line

- The commands `init`, `import`, `build`, `verify`, `inspect`, `diff` and `pack`, their
  options and positional arguments, and the exit codes 0 to 3 keep their meaning. Exit 3 means
  some checks were not verified and none failed; `--expect-not-verified` (decision 0008) is how
  a consumer accepts an exact set of them.
- `--json` documents have the members `rules-corpus --help` and the tests show. A minor release
  may add members to them; a consumer ignores members it does not know. Existing members are not
  removed, renamed or retyped.
- Human-readable output is not a promise.

## The .NET libraries

`RulesCorpus`, `RulesCorpus.Adapters.Text` and `RulesCorpus.Adapters.Xml` multi-target net8.0 and
net10.0. Their public surface is exactly the `PublicAPI.Shipped.txt` beside each project, and
the build fails on any difference from it. Semantic versioning applies: a minor release only
adds to it, a major release may remove. The surface is:

- the adapter contract: `ICorpusAdapter`, `AdapterInput`, `AdapterOutput`, `AdapterSegment`,
  `AdapterSourceSpan`, `PageRange`, `ByteRange`, `DerivationFidelity`, `CorpusAdapterException`
  and the `CorpusLimits` they are given;
- reading a corpus: `CorpusManifest` and the records it exposes, `CorpusFiles`, `ContentDigest`;
- the operations: `CorpusBuilder`, `CorpusVerifier`, `CorpusPacker`, `ManifestDiff`, and their
  reports and options.

What is not public is not promised: the grammar predicates, the canonical JSON writer and the
tar writer are internal.

The `RulesCorpus.Cli` assembly is a tool, not a library; nothing in it is public API.

## Not promised

Segmenting a source differently, a new adapter, a new format and the citation grammar a
consumer builds on top are not part of this contract. The adapters' determinism holds on one
.NET runtime; across runtimes it holds for the patterns and inputs the adapter documents.
