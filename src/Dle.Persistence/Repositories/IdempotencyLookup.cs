namespace Dle.Persistence.Repositories;

/// <summary>
/// The verdict on an <c>Idempotency-Key</c>, and the response to replay when there is one.
/// </summary>
/// <param name="Outcome">What the caller should do.</param>
/// <param name="ResponseStatus">HTTP status of the stored response, when replaying.</param>
/// <param name="ResponseBody">Body of the stored response, replayed verbatim.</param>
public sealed record IdempotencyLookup(
    IdempotencyOutcome Outcome,
    int ResponseStatus,
    string? ResponseBody)
{
    /// <summary>The key is new; handle the request.</summary>
    public static IdempotencyLookup Proceed { get; } =
        new(IdempotencyOutcome.Proceed, 0, null);

    /// <summary>The first attempt is still running.</summary>
    public static IdempotencyLookup InProgress { get; } =
        new(IdempotencyOutcome.InProgress, 0, null);

    /// <summary>The key was reused with a different body.</summary>
    public static IdempotencyLookup Conflict { get; } =
        new(IdempotencyOutcome.Conflict, 0, null);
}
