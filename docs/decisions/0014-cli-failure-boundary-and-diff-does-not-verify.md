# 0014. The CLI's failure boundary, and `diff` does not verify

**Status:** accepted. Decided in the post-1.0 hardening pass, 2026-10-01.

## Evidence

- `Cli.Run` caught `CorpusAdapterException`, `IOException` and `UnauthorizedAccessException`.
  Any other exception escaped as a stack trace with a runtime-defined exit code. Reproduced
  causes: `Path.GetFullPath` throws `ArgumentException` for a path with a null character, and
  `verify /dev/zero` hung, because any existing "file" was read to its end as a packed corpus.
- `diff` loads two manifests and compares them. With a stored artifact tampered in one corpus
  and the manifests untouched it reports equal digests and exits 0, and its help and output
  did not say it had verified nothing (reproduced in the CLI tests).
- No consumer uses `diff` as a trust check: rules-factory and both engines call `build` and
  `verify --rebuild`; `diff` appears only in the README example and the sample check.

## Decision

1. **Boundary.** `Cli.Guard` reports as themselves the failures that are the input's or the
   environment's: a usage error (exit 2); a refusal, a failed check, an adapter's refusal, a
   corpus error and an I/O or permission error (exit 1). Anything else is a defect in this
   tool. It is reported as `internal error: <exception type>: <message>` plus a request to
   report it, exit 1, and under `--json` the usual error document. No stack trace is printed
   and nothing is hidden: the type and message of a defect are visible. Out-of-memory is a
   process failure and propagates.
2. **Causes removed at their source**, not by catching broadly: a path the platform cannot
   represent is a usage error from the one place paths are resolved, and a path that exists but
   is not a regular file is refused as a packed corpus.
3. **`diff` stays a manifest comparison and stays cheap.** It does not verify. Its help says so,
   its human output starts with a note saying neither corpus was verified, and its `--json`
   result carries `verificationPerformed: false`.

Rejected: making `diff` verify both corpora. It would make an inspection command slow and
change what it means, and no consumer asked for it; `verify` already exists and is what a trust
decision should use.

## Compatibility

No schema, digest, packed-format or public .NET API change. Exit codes keep their documented
meanings: the defect case uses exit 1 rather than a new code, so a script that treats nonzero as
failure is unaffected. The `diff --json` result gains one member, which docs/compatibility.md
already allows in a minor release, and consumers ignore members they do not know. The human
output of `diff` gains a first line; human output is not a promise.

## Inference

Exit 1 for a defect conflates it with a failed check for a script that distinguishes them.
That is accepted over adding a fifth exit code: the message says `internal error`, and a new
code would be a larger compatibility event for a case that should not occur.

## What would change it

A consumer that needs to tell a defect from a failure mechanically (a dedicated exit code), or
one that relies on `diff` as a trust check.
