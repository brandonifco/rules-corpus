# Backlog

Ideas that are not current milestone work. An item moves into a milestone only with a named
consumer or a release-blocking invariant (CLAUDE.md principle 6).

- **M4 calibration: FAA Part 107.** Describe `corpus/part107.xml` (eCFR XML, baseline
  `cfr-14-107`, `ecfr-versioner-xml`, as of 2026-01-01) as a corpus with a source-artifact
  baseline, and replace the engine's vendored copy of the factory's intake digest check with
  `rules-corpus verify`. Segmenting it needs an XML adapter (M5). Identity half done:
  `tools/calibration/faa-part-107.corpus.build.json` projects the pinned baseline exactly
  ([record](calibration/2026-09-30-m4-identities.md)); the pinned-commit run and the
  replacement remain.
- **M4 calibration: SRD 5.2.1.** Describe `corpus/srd-5.2.1.txt` as the external `pdftotext`
  24.02.0 derivation of the unstored PDF, segmented by the text adapter with
  `pageMarker ^\{([0-9]+)\}$` and `pagesContiguous true`. Identity half done:
  `tools/calibration/srd-52-combat.corpus.build.json` projects the pinned baseline exactly,
  18373 segments on pages 1-364, no adapter refusal
  ([record](calibration/2026-09-30-m4-identities.md)); the pinned-commit run and the
  replacement remain.
- **rules-factory intake.** `HASH_DERIVATIONS` and `verify_declared_corpus_digest` are copied
  into each engine and have already drifted. Replacing them is the M4 prize. In the engines
  the vendored check is `verify_corpus`; `verify_declared_corpus_digest` is only in
  rules-factory's current intake ([record](calibration/2026-09-30-m4-identities.md)).
- **Citation resolution boundary.** Section-designation, heading-path and page-marker walks
  are duplicated across rules-factory's checkers. They encode citation grammar; decide
  whether any generic part (page lookup by marker) belongs here.
- **M5 second format.** XML is the highest-value candidate (the regulatory consumer's source
  is eCFR XML). PDF only with a pinned, empirically reproducible extractor.
- **Multi-document corpora.** FRCP-style byte-slice derivations of a larger release.
- **Run the tests on net8.0.** The libraries target net8.0 and net10.0, but the test projects
  run on net10.0 only. `Files/FileKind.cs` depends on the runtime's `SystemNative_LStat` shim;
  a one-off probe on the 8.0.31 runtime classified regular files, directories, symbolic
  links, a named pipe and `/dev/zero` correctly (2026-09-30), but nothing in the gate proves
  it. Multi-target the test projects and add the 8.0 runtime to CI.
- **M4 exit 3 policy.** An engine whose corpus includes an unstored artifact or an external
  derivation (srd-52-combat) always gets exit 3 from `verify`. Decide how an engine's gate
  treats that before replacing its intake check.
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
