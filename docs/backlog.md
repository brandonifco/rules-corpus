# Backlog

Ideas that are not current milestone work. An item moves into a milestone only with a named
consumer or a release-blocking invariant (CLAUDE.md principle 6).

- **Calibration copies of the consumers' build definitions.** `tools/calibration/*.corpus.build.json`
  now duplicate, apart from notes and source paths, the definitions each engine commits and its
  gate builds ([record](calibration/2026-10-01-m4-migration.md), remaining duplication 2).
  Either point `run.sh` at the engines' own definitions at a post-migration commit, or retire
  the calibration copies.
- **Citation resolution boundary** ([#7](https://github.com/brandonifco/rules-corpus/issues/7)). Section-designation, heading-path and page-marker walks
  are duplicated across rules-factory's checkers. They encode citation grammar; decide
  whether any generic part (page lookup by marker) belongs here.
- **Further formats.** XML is done (`xml` adapter, decision 0011). HTML, and PDF only with a
  pinned, empirically reproducible extractor, wait for a consumer whose source needs them.
- **Multi-document corpora.** FRCP-style byte-slice derivations of a larger release.
- **Reproducible packages and a `dotnet tool`.** `dotnet pack` of the same sources twice gives
  different `.nupkg` and `.snupkg` hashes for all four projects (checked 2026-10-01 with
  `CI=true`), so "packages reproducible" (plan M7) is not met, and nothing is published. Nothing
  needs it: consumers pin a commit (rules-factory decision 0074) and there is no feed. Do it when
  a consumer needs a package: make the pack deterministic first, and prove it by packing twice.
