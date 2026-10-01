# Backlog

Ideas that are not current milestone work. An item moves into a milestone only with a named
consumer or a release-blocking invariant (CLAUDE.md principle 6).

- **Calibration copies of the consumers' build definitions.** `tools/calibration/*.corpus.build.json`
  now duplicate, apart from notes and source paths, the definitions each engine commits and its
  gate builds ([record](calibration/2026-10-01-m4-migration.md), remaining duplication 2).
  Either point `run.sh` at the engines' own definitions at a post-migration commit, or retire
  the calibration copies.
- **Citation resolution boundary.** Section-designation, heading-path and page-marker walks
  are duplicated across rules-factory's checkers. They encode citation grammar; decide
  whether any generic part (page lookup by marker) belongs here.
- **M5 second format.** XML is the highest-value candidate (the regulatory consumer's source
  is eCFR XML). PDF only with a pinned, empirically reproducible extractor.
- **Multi-document corpora.** FRCP-style byte-slice derivations of a larger release.
- **Core hardening from the 2026-10-01 review (unfixed, low priority).** (a) `CorpusLimits` is
  not validated: a `MaxArtifactBytes` above `Array.MaxLength` or negative reaches
  `new byte[length]` in `DirectoryCorpusFiles` and `PackedCorpusFiles`. (b)
  `PackedCorpusFiles.Load` caps each entry but not the entry count or the total, so a tar of
  many maximum-size entries exhausts memory. Neither is reproduced yet. That review did not read
  `JsonMembers.cs`, `CjValue.cs`, `Vocabulary.cs`, `ManifestRecords.cs`, the `Adapters/`
  interface files or the verification report types, so the core is not comprehensively
  reviewed. Untested: the ustar name split fallback in `PaxTarWriter.UstarName` and the
  `PackedCorpusFiles` limits.
- **CLI findings from the same review, not fixed.** `pack` buffers the archive and writes it
  with `FileMode.CreateNew`, so a write failure after creation would leave a truncated
  `.tar` that a rerun refuses as existing; read from the code, not reproduced, since it needs
  fault injection. `diff` loads manifests without verifying (a tampered stored artifact still
  reports equal digests); say so in the usage text or decide it should verify. Exceptions
  other than `CorpusAdapterException`, `IOException` and `UnauthorizedAccessException` escape
  `Cli.Run` as stack traces.
