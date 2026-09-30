# Backlog

Ideas that are not current milestone work. An item moves into a milestone only with a named
consumer or a release-blocking invariant (CLAUDE.md principle 6).

- **M4 calibration: FAA Part 107.** Describe `corpus/part107.xml` (eCFR XML, baseline
  `cfr-14-107`, `ecfr-versioner-xml`, as of 2026-01-01) as a corpus with a source-artifact
  baseline, and replace the engine's vendored copy of the factory's intake digest check with
  `rules-corpus verify`. Segmenting it needs an XML adapter (M5).
- **M4 calibration: SRD 5.2.1.** Describe `corpus/srd-5.2.1.txt` as the external `pdftotext`
  24.02.0 derivation of the unstored PDF, segmented by the text adapter with
  `pageMarker ^\{(\d+)\}$` and `pagesContiguous true`.
- **rules-factory intake.** `HASH_DERIVATIONS` and `verify_declared_corpus_digest` are copied
  into each engine and have already drifted. Replacing them is the M4 prize.
- **Citation resolution boundary.** Section-designation, heading-path and page-marker walks
  are duplicated across rules-factory's checkers. They encode citation grammar; decide
  whether any generic part (page lookup by marker) belongs here.
- **M5 second format.** XML is the highest-value candidate (the regulatory consumer's source
  is eCFR XML). PDF only with a pinned, empirically reproducible extractor.
- **Multi-document corpora.** FRCP-style byte-slice derivations of a larger release.
