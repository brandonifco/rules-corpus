# Architecture

rules-corpus owns the trustworthy path from authoritative source bytes to an immutable,
addressable, verifiable corpus. It records what was used and how it was transformed. It does
not interpret any of it.

```
authoritative source bytes
        │  acquire (outside; recorded as evidence)
        ▼
corpus.build.json ──► rules-corpus build ──► derived artifacts + corpus.json
                                             │
                          rules-corpus verify / inspect / diff / pack
                                             │
                                             ▼
                       rules-factory, engines (baselines → rules-kernel SourceBaselineId)
```

## Projects

| Project | Owns | References |
|---|---|---|
| `src/RulesCorpus` | identifiers, digests, canonical JSON, manifest and build-definition models, adapter contract, builder, verifier, packing | BCL only |
| `src/RulesCorpus.Adapters.Text` | the `text` adapter | `RulesCorpus` |
| `src/RulesCorpus.Cli` | the `rules-corpus` command | both of the above; also the core's internals, so it reads files through the same path rules and writes JSON through the one canonical writer |

The reference graph is enforced by `tools/repo-checks.py --only layering`.
Why the kernel is not referenced: [decision 0001](decisions/0001-scope-packages-and-layering.md).

## Pipeline

1. **Acquire** happens outside. The build definition records origin and retrieval date as
   evidence; nothing is fetched.
2. **Fingerprint.** The builder digests every stored source before running anything.
3. **Normalize and segment.** An adapter ([contract](adapter-contract.md)) turns one input
   into canonical bytes and segments. The builder validates and measures its output.
4. **Map.** Segments carry source spans back into their input.
5. **Manifest.** The builder writes `corpus.json` with both identities
   ([decision 0002](decisions/0002-two-identities.md)).
6. **Verify.** Offline; three outcomes ([decision 0004](decisions/0004-external-derivations-and-not-verified.md)).
7. **Pack.** A deterministic tar ([decision 0006](decisions/0006-packing-as-deterministic-tar.md)).

The format itself is [corpus-format.md](corpus-format.md).

## Invariants and where they are enforced

| Invariant | Enforced by |
|---|---|
| Core neutrality | `repo-checks.py --only neutrality`; review |
| Immutability | builder refuses to write a source path; tests |
| Digest integrity | verifier; tests |
| Deterministic build | `verify --rebuild`; `tools/sample-corpus/check.sh` (a gate step) builds each sample twice from its sources and compares the outputs and packed archives byte for byte |
| Declared derivation | manifest validation: every derived artifact has exactly one derivation |
| Address integrity | manifest validation: unique segment ids, spans in bounds |
| Mapping integrity | manifest validation: source spans resolve and are in range |
| Offline verification | `repo-checks.py --only determinism` bans network, clock, environment and randomness in the core and the text adapter, and the network in the CLI |
| Public API discipline | PublicApiAnalyzers baselines |
| Documentation truth | `repo-checks.py --only doc-references`; the committed `samples/` are the README's example, and `tools/sample-corpus/check.sh` fails if they differ from what the CLI builds |
