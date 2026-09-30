# rules-corpus

Evidence infrastructure for rules engines. Given retained authoritative source artifacts,
rules-corpus reproducibly produces and verifies a portable, immutable, addressable corpus whose
content identity, derivation history and source mappings are explicit, without containing or
interpreting any domain rules.

**Status:** pre-1.0, under construction (milestones M1–M3 of the
[plan](docs/rules-corpus-design-and-development-plan.docx)).

- [Architecture](docs/architecture.md)
- [Corpus format](docs/corpus-format.md)
- [Adapter contract](docs/adapter-contract.md)
- [Decisions](docs/decisions/)
- [Backlog](docs/backlog.md)

## Validate

```bash
./scripts/validate.sh full
```
