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
