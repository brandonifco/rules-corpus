# 0011. A second adapter package: `xml`

**Status:** accepted. Decided by the lead on 2026-10-01 to satisfy plan milestone M5.

## Evidence

- The plan's M5 asks for a second real format adapter, chosen by the highest-value real
  consumer need, and its M6 requires that no public abstraction remain unvalidated.
  `ICorpusAdapter` had one implementation.
- The regulatory consumer's source is eCFR XML (`corpus/part107.xml`, 79,880 bytes, 61 `DIV8`
  section elements each with an `N` attribute holding the section number). Before this record
  its calibration corpus had no segments: its `contentDigest` covered the baseline alone
  ([record](../calibration/2026-09-30-m4-identities.md)).
- The contract (docs/adapter-contract.md) needed no change to carry it: canonical bytes, a
  media type, fidelity, byte-span segments with byte-range source spans.

## Decision

1. A new package, `RulesCorpus.Adapters.Xml` (net8.0; net10.0), references only
   `RulesCorpus`. It is separate from `RulesCorpus.Adapters.Text`, as decision 0001 set up:
   an adapter's dependencies and release cycle are its own.
2. Its id is `xml`, version `1`. It is parameterized by two names, `segmentElement` and
   `idAttribute`, and knows no vocabulary of any format built on XML. The canonical artifact
   is the input unchanged.
3. The CLI references it and ships it beside `text`.
4. It refuses a `DOCTYPE`, a byte-order mark, a carriage return, a non-UTF-8 declaration and
   anything not well-formed, and never reproduces the parser's localized message, so every
   refusal is the same on every machine.

## Compatibility

New public surface, declared in `PublicAPI.Unshipped.txt` (`XmlAdapter` and its four members).
No manifest schema, serialized form or core type changes. An existing corpus is unaffected.

## Inference

- The adapter contract is demonstrated by two materially different adapters: one scans lines
  with regular expressions and records losses; the other parses a tree-structured language,
  records none, and maps spans one to one. Both run through the same core validation without
  special cases.
- A segment per element is the narrowest unit a real consumer needs. Nesting was refused
  rather than defined because the real source has none, and a rule for it would be speculative.

## What would change it

A consumer that needs nested or overlapping segments, a locator drawn from the document, or
canonicalization of XML (attribute order, whitespace). None asks for any of it today.
