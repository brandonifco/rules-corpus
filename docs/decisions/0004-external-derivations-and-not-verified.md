# 0004. External derivations, unstored artifacts, and "not verified"

**Status:** accepted.

## Evidence

- The SRD corpus text is `pdftotext` 24.02.0 output from a 6 MB PDF plus page markers
  (rules-factory `examples/srd-52-combat/extract.py`). The PDF is committed only in
  rules-factory; the engine commits the text. Its checker prints "NOT VERIFIED" when the
  pinned poppler is absent rather than failing or passing.
- PDF extraction is not reproducible across extractor versions (plan §17).

## Decision

- A derivation performed outside rules-corpus is declared in `external[]` with its tool and
  version, recorded with `reproducibility: external`, and verified by digest. `verify
  --rebuild` reports it **not verified**.
- An artifact may be declared by identity alone (`stored: false`, bytes and digest). Its bytes
  are **not verified**.
- Not verified is a third outcome, never ok. The CLI exits 3 when anything is not verified,
  unless `--allow-not-verified` is given.
