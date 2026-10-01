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
