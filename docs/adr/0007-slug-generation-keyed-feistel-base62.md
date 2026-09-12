# ADR-0007 — Slug generation: keyed Feistel permutation of a sequence, base62, 8 characters

**What this is:** how the 8-character public slug is produced, and why it is neither random nor a counter.
**Who it is for:** anyone who wants to change the slug length or format — which changes every public URL — and security reviewers assessing enumeration.

## Status

Accepted

## Date

Decided: in the specification ([§B.4 ADR-007](../zadanie.md#adr-007--generovanie-slugov-kľúčovaná-permutácia-sekvencie--base62-8-znakov)) · Recorded: 2026-09-11

## Context

Start with the arithmetic, because this is where the usual mistake is made. Eight base62 characters give 62⁸ ≈ 2.18 × 10¹⁴ values — **about 47.6 bits**. A 64-bit Snowflake ID **does not fit** in 8 characters; it needs 11. So either the slug is 11 characters or its source is something other than the row ID.

## Decision

- The internal `links.id` stays a 64-bit Snowflake (ordering, sharding, index locality).
- **The slug is generated independently:** a global PostgreSQL sequence limited to **47 bits** (1.4 × 10¹⁴ values) → a **keyed Feistel permutation** over the 47-bit space (4 rounds, round function HMAC-SHA-256 with a secret key) → base62, **exactly 8 characters**.
- A custom slug is allowed, guarded by the unique index `uq_links_domain_slug (domain_id, slug)`, and must be distinguishable from the generated shape — the specification's example: at least 3 characters and never the 8-character generated form (e.g. ≥ 9).
- Enumeration is **not primarily defended by slug length** but by access control and rate limiting: the same response for a non-existent and an unauthorised link, a separate budget for 404s per IP ([§E.9](../zadanie.md#e9-rate-limity-a-kvóty--konkrétne-hodnoty)), and no analytics reachable without authentication. The keyed permutation is the second layer, not the only one.
- Event-table IDs are **UUIDv7** (`uuidv7()` in PG 18) — time-ordered, BRIN-friendly.

## Consequences

- Positive: a Feistel network is a **bijection** — every sequence value maps to exactly one slug, a collision is impossible by construction, and link creation needs no retry (deterministic response time).
- Positive: the permutation is keyed, so a slug reveals neither the next slug nor the number of links created.
- Negative: the key (`DLE_MASTER_SECRET`-derived) is now part of the URL format. Losing it does not break existing links (they are stored), but rotating it changes the mapping for new ones; there is no way to "re-derive" a slug from the sequence without it.
- Negative: the format is fixed by public URLs. The specification's own warning applies: decide at M1, not later.
- Verification: the Feistel bijection is **proven exhaustively at narrow widths** in the unit suite (part of the 1 419 passing tests). One related defect is worth knowing: the NFKC normalisation of custom slugs was a silent no-op under `InvariantGlobalization`, so the homoglyph defence never ran; the suite found it and it is fixed in the commit history.

## Alternatives considered

| Alternative | Why it lost |
|---|---|
| Random base62 | Birthday problem: at 6 characters (62⁶ ≈ 5.7 × 10¹⁰) the collision probability reaches 1 % after ~34 000 codes; at 8 characters and 10⁹ links the expected number of collisions is in the thousands — survivable with a unique constraint + retry, but it is an extra round trip on every creation. The specification names "8 random characters + unique constraint + retry" as the acceptable simplification *if* Feistel proved to be needless complexity; it did not |
| Plain sequential counter | Trivially enumerable and discloses link volume |
| Hashids / Sqids | Not a hash but a reversible encoding — Sqids says so in its own FAQ, and the scheme was cryptanalysed in 2015. As a security mechanism it is self-deception |
| 11-character slug carrying the Snowflake | Fits, but longer URLs and the slug would leak creation time and ordering |

## References

- [docs/zadanie.md §B.4 ADR-007](../zadanie.md#adr-007--generovanie-slugov-kľúčovaná-permutácia-sekvencie--base62-8-znakov)
- [docs/zadanie.md §B.6.3](../zadanie.md#b63-deferred-deep-link--ios-bez-determinizmu-z-platformy) — the same Feistel construction embeds a timestamp in `click_id` (see [architecture/request-flows.md](../architecture/request-flows.md))
- [docs/zadanie.md §E.3](../zadanie.md#e3-ochrana-proti-zneužitiu-redirektora) — redirector abuse defences
- `src/Dle.Crypto`, `src/Dle.Domain`
