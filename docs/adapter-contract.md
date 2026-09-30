# Adapter contract

The core knows nothing about how a format becomes canonical content. An adapter does, behind
this contract, in its own assembly. The core runs it, measures what it produced, and records
the result; an adapter never computes a digest the manifest trusts.

```csharp
public interface ICorpusAdapter
{
    string Id { get; }          // corpus id grammar: "text"
    string Version { get; }     // "1"; a behaviour change is a new version
    bool IsDeterministic { get; }
    AdapterOutput Derive(AdapterInput input);
}
```

- `AdapterInput` carries the input artifact's id and bytes, the parameters from the build
  definition (ordinal-sorted, string to string) and the build's `CorpusLimits`.
- `AdapterOutput` carries the canonical bytes, the output media type, the fidelity, the losses
  (empty exactly when lossless) and the segments in document order. Each segment is an id, a
  byte span of the canonical bytes, an optional locator, and source spans into the **input**
  artifact (pages, a byte range, or both); the core fills in the input's artifact id.
- An adapter signals refusal by throwing `CorpusAdapterException`. It never returns partial
  output. An unknown parameter is a refusal, not something to ignore. Any other exception an
  adapter throws is still treated as a refusal of that input, naming the adapter and the
  exception type, so a hostile corpus cannot crash the build or the verifier through an
  adapter bug; only process failures such as running out of memory propagate.
- The core validates everything the adapter returns — segment ids, bounds, UTF-8 boundaries,
  uniqueness, source-span ranges against the input — and refuses the build if any of it is
  wrong. An adapter's claims are checked, not trusted.
- `IsDeterministic` must be `true` for an adapter the build runs; a non-deterministic
  transformation belongs in the build definition's `external[]`, where it is declared as
  evidence rather than reproduced.

Adapter identity and version are recorded in every derivation as `tool`. Changing what an
adapter emits for the same input and parameters is a new `Version`, and a compatibility event
(see [corpus-format.md](corpus-format.md#the-two-identities)).

## Security

Imported content is untrusted. Adapters never execute embedded content, never follow network
references, and bound their work by `CorpusLimits`. Regular expressions supplied as parameters
run with `RegexOptions.NonBacktracking`, so matching time is linear in the input.

## The text adapter (`text`, version 1)

Package `RulesCorpus.Adapters.Text`. Input: UTF-8 text. Output media type `text/plain`.

### Canonicalization

1. Decode strictly as UTF-8. Invalid UTF-8 is a refusal.
2. `bom`: `reject` (default) refuses input that starts with a byte-order mark; `strip` removes
   it and records the loss `byte-order mark removed`.
3. `newlines`: `lf` (default) rewrites CRLF and lone CR as LF and records
   `line endings normalized to LF` if anything changed; `preserve` refuses input containing CR.
4. No Unicode normalization. Under invariant globalization `string.Normalize` silently
   returns its input unchanged, so offering it would record a transformation that did not
   happen ([decision 0003](decisions/0003-no-unicode-normalization-in-text-v1.md)).

The canonical bytes are the result, encoded as UTF-8 without a byte-order mark. Fidelity is
`lossless` when no loss was recorded, else `lossy-traceable`.

### Pages

`pageMarker` (optional) is a regular expression, matched per line, with one capturing group
that is the page number as decimal digits. A line that matches in its entirety is a **marker
line**; a match inside a longer line is an **inline marker**. The page in effect at any byte is
the last marker at or before it. `pagesContiguous` (`true`/`false`, default `false`) refuses
markers that do not run 1, 2, 3, … in order. Content before the first marker has no page.

### Segmentation

`segmentation` selects one of:

- `whole` (default): one segment covering the entire canonical text, id `all`.
- `blocks`: blocks separated by one or more blank lines (lines empty or only spaces and tabs).
  Marker lines are block boundaries and are never inside a segment. A segment spans its first
  to last non-blank character's line, inclusive of the last line's text but not its newline.
  Ids are `b1`, `b2`, … in order, or `p{page}.b{k}` (k restarting at 1 each page) when
  `pageMarker` is set and the block starts on a page; locator `p. {page}` for those.
- `headings`: `headingPattern` is a regular expression matched against each line; a matching
  line starts a segment, which runs to the byte before the next matching line's start or the
  end of the text, with trailing newlines excluded. A named group `id` gives the segment id
  and is required; a named group `locator`, if present, gives the locator, otherwise the whole
  heading line (without its newline) is the locator. Text before the first heading is not
  segmented. A segment id that is invalid or repeated is a refusal.

Every segment carries source spans into the input: the byte range in the input that the
segment's canonical bytes came from, and, when `pageMarker` is set and the segment starts on
a page, `pages` from the page at its first byte to the page at its last byte.

Any other parameter key or value is a refusal. `segmentation` values not listed are a refusal.

### Refusals the rules above imply

The adapter takes the strict reading wherever the rules leave room:

- `pagesContiguous` means the markers run 1, 2, 3, … starting at 1.
- A page number is ASCII digits without a leading zero, fitting in a 32-bit integer.
- Whether a line is a marker line is decided by the first match on it, which must cover the
  whole line.
- Any repeated segment id is a refusal, including `p{page}.b{k}` repeated because a page
  number repeats.
- A segment whose page at its last byte precedes the page at its first byte is a refusal.
- A parameter that would have no effect is a refusal: `headingPattern` without
  `segmentation` `headings`, or `pagesContiguous` without `pageMarker`.
- Producing no segments is a refusal: empty input under `whole`, no blocks under `blocks`, a
  heading pattern that matches nothing.
- A `locator` group that is present in the pattern but empty or unmatched is a refusal.
- Under `headings` and `whole`, marker lines are part of segment content; only `blocks`
  excludes them.

### A limit on the determinism claim

Regular-expression classes such as `\d`, `\w` and case-insensitive matching use the Unicode
tables built into the .NET runtime, which can differ between runtime versions. Same input and
parameters give the same output on one runtime; across runtimes that holds only for patterns
that avoid those constructs. Write ASCII classes (`[0-9]`, `[A-Za-z]`) in patterns a corpus
depends on.
