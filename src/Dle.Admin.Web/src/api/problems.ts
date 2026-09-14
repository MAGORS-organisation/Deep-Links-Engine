/**
 * Problem type identifiers, mirrored from src/Dle.Domain/Contracts/ProblemCodes.cs. The server
 * puts one of these in the `type` member of every RFC 9457 problem document, and the console
 * branches on them for field-level feedback instead of pattern matching on English text.
 */
export const PROBLEM_BASE = 'https://docs.dle.dev/problems/';

export const ProblemCodes = {
  ValidationFailed: `${PROBLEM_BASE}validation-failed`,
  SlugTaken: `${PROBLEM_BASE}slug-taken`,
  SlugInvalid: `${PROBLEM_BASE}slug-invalid`,
  UnsafeTarget: `${PROBLEM_BASE}unsafe-target`,
  RateLimited: `${PROBLEM_BASE}rate-limited`,
  MissingDefaultRule: `${PROBLEM_BASE}missing-default-rule`,
  InvalidRoutingRules: `${PROBLEM_BASE}invalid-routing-rules`,
  DomainNotVerified: `${PROBLEM_BASE}domain-not-verified`,
  DomainTaken: `${PROBLEM_BASE}domain-taken`,
  IdempotencyConflict: `${PROBLEM_BASE}idempotency-conflict`,
  Unauthorized: `${PROBLEM_BASE}unauthorized`,
  Forbidden: `${PROBLEM_BASE}forbidden`,
  ClaimCodeInvalid: `${PROBLEM_BASE}claim-code-invalid`,
  LinkQuarantined: `${PROBLEM_BASE}link-quarantined`,
  DependencyUnavailable: `${PROBLEM_BASE}dependency-unavailable`,
  /** Emitted by the SPA fallback for an unmatched API route; not in ProblemCodes.All. */
  NotFound: `${PROBLEM_BASE}not-found`,
} as const;

export type ProblemCodeName = keyof typeof ProblemCodes;

/** Codes the console can raise itself when no server answer exists. */
export type LocalProblem = 'network' | 'no_credential' | 'unexpected_body';

export type ProblemKey = ProblemCodeName | LocalProblem | 'unknown';

const byUri = new Map<string, ProblemCodeName>(
  (Object.keys(ProblemCodes) as ProblemCodeName[]).map((name) => [ProblemCodes[name], name]),
);

/** Resolves a `type` URI to its symbolic name, or `unknown` for a type this build does not know. */
export function problemKeyOf(type: string | undefined | null): ProblemKey {
  if (!type) {
    return 'unknown';
  }
  return byUri.get(type) ?? 'unknown';
}
