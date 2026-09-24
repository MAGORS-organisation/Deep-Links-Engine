# ADR-0002 — Minimal APIs and vertical slices

**What this is:** the web framework and the code organisation of both hosts.
**Who it is for:** anyone adding an endpoint, and anyone wondering why there are no controllers.

## Status

Accepted

## Date

Decided: in the specification ([§B.4 ADR-002](../zadanie.md#adr-002--minimal-apis--vertical-slice-architecture)) · Recorded: 2026-09-11

## Context

The resolve path has an 8 ms p50 budget ([NFR-01](../zadanie.md#a5-nefunkčné-požiadavky)), so per-request framework overhead matters. The control plane has dozens of small, independent operations (create link, verify domain, rotate key) that change at different rates. Layered architectures (controllers → services → repositories) spread one feature across four folders and make every change a cross-cutting one.

## Decision

ASP.NET Core **Minimal APIs** in both hosts, organised as **vertical slices by feature** — `Features/Links/CreateLink.cs` rather than `Controllers/LinksController.cs` + `Services/LinkService.cs`. Concretely:

- Validation uses the native .NET 10 Minimal API validation (`AddValidation()` + `[ValidatableType]` with the source generator), not FluentValidation or MVC model state.
- Errors are RFC 9457 problem documents through `IProblemDetailsService`; the problem type identifiers live in [`src/Dle.Domain/Contracts/ProblemCodes.cs`](../../src/Dle.Domain/Contracts/ProblemCodes.cs).
- The API description is OpenAPI 3.1 from `Microsoft.AspNetCore.OpenApi`, served with **Scalar** at `/scalar/v1` on the control host (`:8081`).
- Wire JSON is `snake_case` through one shared naming policy.

## Consequences

- Positive: the lowest per-request overhead ASP.NET Core offers; no MVC pipeline, no reflection-driven model binding on the hot path — which also keeps the edge AOT-ready ([ADR-0012](0012-native-aot-deferred-to-v2.md)).
- Positive: one file per operation means one place to read when a behaviour is in question.
- Negative: no filters/attributes ecosystem from MVC; cross-cutting concerns (auth, rate limits, tenancy) are endpoint filters and route groups, and they must be applied deliberately — the contract suite exists partly to catch a group that forgot one.
- Verification: both hosts start and serve their OpenAPI documents; the contract suite checks the documented shapes; the security suite covers the 404/410/302 paths through `WebApplicationFactory`. The current test counts are in the CI summary.

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| MVC controllers | Adds reflection and the MVC pipeline for no gain on either host; heavier to keep AOT-compatible |
| Swashbuckle for the UI | Dropped from the .NET templates since .NET 9 and effectively in maintenance; Scalar sits on the built-in OpenAPI 3.1 output |
| Layered (n-tier) organisation | Every feature spread across layers; the specification chose feature folders explicitly |

## References

- [docs/zadanie.md §B.4 ADR-002](../zadanie.md#adr-002--minimal-apis--vertical-slice-architecture)
- [docs/zadanie.md §C.1](../zadanie.md#c1-štruktúra-riešenia) — `Features/…` layout; [§C.3.1](../zadanie.md#c31-hot-path--resolve-endpoint) — the resolve endpoint
- [docs/zadanie.md §B.7](../zadanie.md#b7-api-kontrakt) — the API contract this framework serves
