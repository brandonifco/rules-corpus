# 0008. Consumers pin the checks they accept as not verified

**Status:** proposed (issue #3). Merging the pull request that adds this record accepts it.

## Evidence

- Decision 0004 makes not verified a third outcome and exits 3 for it unless
  `--allow-not-verified` is given. The M4 calibration
  ([record](../calibration/2026-09-30-m4-identities.md)) shows srd-52-combat always gets it:
  `verify --rebuild` reports `artifact srd-pdf` (declared by identity only) and
  `rebuild srd-extraction` (external derivation by pdftotext 24.02.0) as not verified. They
  stay not verified wherever it runs, because rules-corpus neither holds the PDF nor runs
  pdftotext. An engine gate that requires exit 0 can therefore never pass.
- When no check fails, `CorpusVerifier` reports not verified in only two places: an artifact
  the manifest declares `stored: false`, and a derivation the manifest declares external.
  Every other not-verified check it emits ("not examined: the manifest did not read" and the
  like) comes with a failed check, and a failure exits 1 whatever the switches say. So
  *which* checks are not verified is a property of what the manifest declares, and it is
  stable for a given build definition.
- `--allow-not-verified` accepts any number of not-verified checks. For example, a build
  definition that also declared the SRD text `stored: false` (bytes and digest only) would
  still project the same baseline, because the baseline names the artifact's digest, and
  `verify --allow-not-verified` would exit 0. The engine's own corpus bytes would never be
  checked.

## Decision

1. `verify` gains `--expect-not-verified <check>[,<check>...]`. The names are the check
   names exactly as `verify` prints them, for example `artifact srd-pdf,rebuild
   srd-extraction`. Check names are built from ids, which cannot contain a comma.
2. With it, `verify` exits 0 only when no check failed and the set of not-verified checks
   **equals** the named set. It exits 1 when a check failed, when a not-verified check is not
   named ("unexpected"), or when a named check is not reported not verified ("missing"). A
   missing check fails too, even when the reason is an improvement such as the PDF becoming
   stored: the pin no longer describes the corpus, and updating it is a visible,
   one-line change in the consumer.
3. `--json` output gains `expectedNotVerified` (`expected`, `unexpected`, `missing` in the
   caller's order and the report's order, and `met`) when the option is given. `outcome`
   still reports the verifier's outcome (`not-verified`), just as it does under
   `--allow-not-verified`. `exitCode` reports the decision.
4. Combining the option with `--allow-not-verified` is a usage error (exit 2), and so is an
   empty, space-padded or repeated name.
5. `--allow-not-verified` stays. It is for the corpus author, who can see every check, and
   for `pack`. A consumer's gate should use `--expect-not-verified`.

Inference: this is the whole of what the consumer's side needs from rules-corpus. How an
engine gets a corpus and the tool is issue #4. The pin itself (the list of names) belongs in
the consumer, next to the baseline it already pins, because it is a statement about which
evidence that consumer agreed to take on trust.

## Alternatives rejected

- **The consumer compares `verify --json`'s check list itself.** This needs no new surface,
  but every engine would carry the comparison in its vendored intake. That is the same
  duplicated corpus logic M4 exists to remove (the drift in `HASH_DERIVATIONS`).
- **Accept not verified only when it comes from a declaration** (`stored: false`, external).
  As the evidence shows, that is every not-verified check without a failure, so this is
  `--allow-not-verified` under another name, and it has the same hole.
- **Pin the manifestDigest instead.** It covers the build definition's notes and changes
  with every manifest member added before 1.0 (calibration record). That is too brittle for
  a gate, and it still would not say which evidence was taken on trust.

## Compatibility

A new CLI option and a new optional member of `verify --json` output. No change to the
manifest, canonical JSON, digests or public types. Existing invocations behave as before.
The CLI's option set and JSON output become part of the compatibility contract at M6.

## What would change this

A consumer whose accepted not-verified set is not fixed for a given build definition. For
example, a check that is not verified only on some machines would need a different rule.
