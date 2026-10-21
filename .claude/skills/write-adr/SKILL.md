---
name: write-adr
description: Template and rules for writing or superseding an Architecture Decision Record in docs/adr for the Vehicle Cache (metadata, Context, Decision, Alternatives, Consequences). Use whenever a design decision is made or changed, before the code that depends on it.
---

# Write an ADR

1. Pick the next number (`ls docs/adr`). File name: `NNNN-kebab-title.md`.
2. One decision per ADR. If you are writing "and" in the title, split it.
3. Fill the template below. Keep it under ~60 lines — reviewers read nine of these.
4. Add the row to `docs/adr/README.md`.
5. If it changes an existing decision: new ADR with `Supersedes: ADR-xxxx`, and set the old one's status to `Superseded by ADR-yyyy` (never rewrite history).
6. Update spec / C4 / test-behaviors in the same commit if they are affected.
7. If an AI proposal was rejected along the way, add a row to `docs/06-ai-workflow.md`.

```markdown
# ADR-NNNN — <decision as a statement>

- **Status:** Proposed | Accepted | Superseded by ADR-xxxx
- **Date:** YYYY-MM-DD
- **Deciders:** Higor Buiatti
- **Related:** <spec §, TB ids, other ADRs>
- **Supersedes:** <optional>

## Context
<forces at play; what makes this non-obvious; constraints from the brief>

## Decision
<what we do, precise enough to implement and to test>

## Alternatives considered
| Option | Rejected because |
|---|---|

## Consequences
+ <what gets easier / guaranteed>
− <costs, limits, what would make us revisit>
```

Quality bar: every "Rejected because" names a **concrete failure scenario or cost**, not a preference. Every Decision is testable — link the TB that proves it.
