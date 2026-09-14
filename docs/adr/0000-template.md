# ADR-NNNN — Short title, a noun phrase

**What this is:** one architecture decision, recorded so it can be challenged with the same facts it was taken on.
**Who it is for:** the reviewer of the change that implements it, and whoever wants to reverse it later.

## Status

Proposed | Accepted | Accepted, deviation | Accepted, deferred | Deprecated | Superseded by `ADR-MMMM` (`MMMM-title.md`) | Rejected

## Date

Decided: YYYY-MM-DD · Recorded: YYYY-MM-DD

## Context

What forces are at play: the workload, the platform constraint, the legal fact, the number. Write the number, not the adjective — "p50 ≤ 8 ms" rather than "fast".

## Decision

What was decided, stated so that a reader can tell whether a given piece of code complies. Use a table where the decision has several branches.

## Consequences

- Positive: what becomes easier or safer.
- Negative: what becomes harder, what it costs to reverse, what must now be maintained.
- Verification: how the repository shows the decision is actually in force (a test, a build flag, a CI gate) — or the honest statement that it has not been verified yet.

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| … | … |

## References

- [docs/zadanie.md §X.Y](../zadanie.md#xy-heading-anchor) — the section this record derives from
- Code that implements or is constrained by the decision
- External facts relied on (release dates, licence changes, guidelines), with dates
