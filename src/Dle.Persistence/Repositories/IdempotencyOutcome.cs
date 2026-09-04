namespace Dle.Persistence.Repositories;

/// <summary>
/// What an <c>Idempotency-Key</c> means for the request carrying it (§B.7.3).
/// </summary>
public enum IdempotencyOutcome
{
    /// <summary>The key is new. Handle the request, then store its response.</summary>
    Proceed = 0,

    /// <summary>
    /// The same request is already being handled by another call that has not finished. The client
    /// retried before the first attempt answered; answering it now would run the operation twice.
    /// </summary>
    InProgress = 1,

    /// <summary>The key has a stored response. Replay it verbatim.</summary>
    Replay = 2,

    /// <summary>
    /// The key was replayed with a different body. That is a client mistake, not a retry, and
    /// answering it with the first response would hide it
    /// (<see cref="Dle.Domain.Contracts.ProblemCodes.IdempotencyConflict"/>).
    /// </summary>
    Conflict = 3,
}
