# 0007. Verification binds every byte a corpus ships

**Status:** accepted. Decided by the lead on 2026-09-30 after review.

## Evidence

A review reproduced both gaps against the M1 core (commit 7803e6a):

- `corpus.build.json` was checked only for being present, valid and naming the same
  `corpusId`. Replacing it after a build with one that declared a different source (id
  `zzz`, path `elsewhere/z.txt`, media type `application/pdf`, another origin) and no
  derivations or baselines still passed `verify --rebuild`: the rebuild re-runs the
  manifest's derivations, never the definition's, and nothing compared the two. The file is
  packed into the tar, so a corpus could ship a build definition that does not build it.
- `corpus.json` was read with any whitespace (by design: readers are lenient), and nothing
  required the file on disk to be the writer's form. A reformatted manifest verified and
  packed to a different archive than the original, so decision 0006's claim that the tar's
  digest is the one that means something did not hold: two archives of one manifest could
  differ in `corpus.json`'s whitespace and in `corpus.build.json`'s bytes.

## Decision

1. **`corpus.json` is exactly the writer's form.** Verification fails (check
   `manifest-form`) unless the file's bytes equal the canonical indented form of the manifest
   they parse to. `CorpusManifest.Parse` still accepts any whitespace; only verification of a
   shipped corpus requires the one form. The writer stays the only formatter.
2. **`corpus.build.json` is bound by digest.** The manifest gains a required member,
   `buildDigest`: the SHA-256 of the exact bytes of the build definition it was built from.
   It is covered by `manifestDigest` (every manifest member except `manifestDigest` is) and
   not by `contentDigest`, which stays the baselines and segment addressing only
   (decision 0002). Verification fails when the file does not digest to it.
3. **The build definition declares exactly what the manifest records.** Verification fails
   (check `build-definition`) on any difference, in order: `corpusId`; for each source, the
   artifact it becomes (id, path, stored, media type, declared bytes and digest when unstored,
   origin, retrieved, notes, or `derivedBy` for an external output); for each external
   derivation, its record (id, inputs, output, tool, parameters, fidelity, losses); for each
   adapter derivation, its output artifact (id, path, `derivedBy`) and its record (id, adapter
   as `tool.id`, the one input, output, parameters); and the baselines. What the definition
   does not declare is taken from the manifest and checked elsewhere: stored bytes and digests
   against the files, and an adapter's version, media type, fidelity and losses by
   `--rebuild`. The builder and the check construct records with the same functions, so they
   cannot disagree about what a definition means.

The rejected alternative for (2) was to accept whitespace variance in `corpus.build.json` and
compare semantically only. Then two packs of one manifest could still differ, and the
hand-written file would be the one byte stream in the archive that nothing identifies.

## Compatibility

Adding a required manifest member changes the schema, and every existing `corpus.json` fails
to read until rebuilt. The schema string stays `rules-corpus/manifest/1`: nothing has shipped
(`PublicAPI.Shipped.txt` is empty and the version is `0.1.0-dev`), so there is no reader of an
earlier schema 1 to stay compatible with. Committed sample corpora must be rebuilt.
`CorpusManifest.BuildDigest` is new public surface.

## Inference

Binding the build definition by digest means a whitespace edit to it is a new manifest
identity. That is intended: the file is shipped evidence, and an edit to shipped evidence
should be visible.

## What would change it

A consumer that needs to edit a shipped build definition without changing the manifest
identity, or a canonical form for `corpus.build.json` that a hand-editor can be held to. No
consumer asks for either today.
