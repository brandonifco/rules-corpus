# 0006. A packed corpus is a deterministic tar

**Status:** accepted.

## Decision

`rules-corpus pack` writes an uncompressed PAX tar with sorted entries and fixed metadata
(docs/corpus-format.md, Packing). `verify`, `inspect` and `diff` accept a packed corpus
wherever they accept a directory.

## Why

The same corpus must pack to the same bytes. Deflate output depends on the compressor
implementation, which .NET changed in 9.0 (zlib to zlib-ng); an uncompressed archive has no
such dependency. Consumers that want compression can compress the tar; the tar's digest is
the one that means something.

That holds because verification binds every byte in the archive to the manifest
([decision 0007](0007-verification-binds-every-shipped-byte.md)): stored artifacts by digest,
`corpus.build.json` by `buildDigest`, and `corpus.json` by being exactly the writer's form. Two
packs that verify with the same `manifestDigest` are the same bytes. Before 0007 they need not
have been: `corpus.json` could be reformatted and `corpus.build.json` rewritten without
failing verification.

## Evidence: why the writer is ours

On SDK 10.0.112, `System.Formats.Tar`'s `TarWriter` names every PAX extended-header entry
`PaxHeaders.<process id>/<name>`: two runs packing the same entry wrote `PaxHeaders.1117776`
and `PaxHeaders.1117821`. Two packs of one corpus from two processes would differ, and a test
that packs twice in one process would not notice. `RulesCorpus` therefore writes the headers
itself (a fixed extended-header name, a `path` record, and the fixed ustar fields above) and
uses `TarReader` only to read. A test pins the digest of a small packed archive, so a change
to the packed form fails the build; GNU tar 1.35 and Python's `tarfile` read that archive
with the documented metadata.
