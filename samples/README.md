# Sample corpora

Three corpora built with the `rules-corpus` CLI and committed with their outputs, so the
example in the [README](../README.md) is real. Each directory holds the hand-written
`corpus.build.json`, the stored sources under `sources/`, and what `rules-corpus build` wrote:
the canonical text under `canonical/` and the manifest `corpus.json`.

`tools/sample-corpus/check.sh` is the proof. It copies each sample's `corpus.build.json` and
stored sources (never the built outputs) into a fresh directory, builds, verifies with
`--rebuild`, compares every built file byte for byte with what is committed here, builds a
second copy and compares again, packs both and compares the archives, verifies an archive,
diffs the two builds, and inspects one segment. `scripts/validate.sh` runs it, so a change to
the code that would change a single byte of these outputs fails the gate until the samples
are rebuilt and the change is reviewed.

To rebuild a sample after a deliberate change: `rules-corpus build --dir samples/<name>`.

## regulatory: every artifact stored, verifies ok

[regulatory/](regulatory/) is a plain-text rendering of five sections of 14 CFR part 107 (the
part heading and sections 107.1, 107.2, 107.9, 107.11 and 107.12), taken from the eCFR XML as of
2026-01-01. A work of the United States Government, in the public domain.

- One acquired source, `cfr-14-107-text`, whose `origin` is the eCFR versioner URL for part 107
  as of 2026-01-01. Its notes say what the rendering changed. No `retrieved` date is recorded,
  because none was kept when the excerpt was made, and the tool never invents one.
- One `text` adapter derivation with `segmentation` `headings` and `headingPattern`
  `^§ (?<id>107\.[0-9]+)`: five segments, `107.1` to `107.12`, each located by its heading line.
  The input is already LF-only UTF-8 without a byte-order mark, so the derivation is lossless and
  the canonical text is byte-identical to the source.
- One baseline, source id `cfr-14-107-excerpt`, naming the acquired text with hash derivation
  `ecfr-versioner-xml-plain-text-excerpt-107.1-107.12` and `asOf` 2026-01-01.

`rules-corpus verify samples/regulatory --rebuild` reports every check ok and exits 0.

## regulatory-xml: the same part, as XML, segmented by the `xml` adapter

[regulatory-xml/](regulatory-xml/) is the part heading, the Subpart A heading and sections
107.1 and 107.2 of 14 CFR part 107, cut from the eCFR versioner XML as of 2026-01-01 with the
elements unchanged. A work of the United States Government, in the public domain.

- One acquired source, `cfr-14-107-xml`, and one `xml` adapter derivation with `segmentElement`
  `DIV8` and `idAttribute` `N`: two segments, `107.1` and `107.2`, each one `DIV8` element from
  its start tag to its end tag. The canonical artifact is the source's bytes, so the derivation
  is lossless and a segment's source span is the same byte range as its span of the canonical
  artifact.
- The same derivation over the whole part, 61 sections, is the M5 calibration
  ([record](../docs/calibration/2026-10-01-m5-xml.md)).

`rules-corpus verify samples/regulatory-xml --rebuild` reports every check ok and exits 0.

## rulebook: an unstored original, verifies as not verified

[rulebook/](rulebook/) is an excerpt of the System Reference Document 5.2.1 text: the last block
of page 190, pages 191 and 192, and the start of page 193. It is CC-BY-4.0; see
[rulebook/NOTICE](rulebook/NOTICE) for the attribution, which is also recorded in the PDF's
acquisition notes so it travels inside `corpus.json` and any packed copy.

- `srd-pdf`: the official PDF, declared by identity only (`stored: false`, 6031375 bytes and its
  SHA-256), with its origin and retrieval date. The PDF is retained in rules-factory, not here.
- `srd-text`: the stored text, produced from the PDF outside rules-corpus. The `external`
  derivation `srd-extraction` records the tool (pdftotext 24.02.0), its command and the page
  marker convention, fidelity `lossy-traceable`, and what was lost, including that this is an
  excerpt.
- One `text` adapter derivation with `segmentation` `blocks` and `pageMarker` `^\{([0-9]+)\}$`:
  blank-line blocks with ids like `p191.b1` and locators like `p. 191`, each mapped back to its
  bytes in `srd-text` and to its pages. The first block, `b1`, precedes the first marker line in
  the excerpt, so it has no page.
- One baseline, source id `srd-5.2.1-excerpt`, naming the extracted text with hash derivation
  `srd-5.2.1-pdftotext-24.02.0-page-marked-excerpt-190-193`.

The PDF's bytes are not in the corpus, so verification cannot examine them, and with
`--rebuild` the external extraction cannot be re-run. Both checks report **not verified**,
never ok: `rules-corpus verify samples/rulebook` exits 3, and exits 0 only with
`--allow-not-verified` ([decision 0004](../docs/decisions/0004-external-derivations-and-not-verified.md)).
That is the point of this sample. Packing it likewise needs `--allow-not-verified`.
