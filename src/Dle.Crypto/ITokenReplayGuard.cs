namespace Dle.Crypto;

/// <summary>
/// Remembers the identifiers of single use tokens so that one cannot be presented twice
/// (§E.4.2: replay protection over <c>jti</c>, with a time to live).
/// </summary>
/// <remarks>
/// The seam exists because the durable implementation belongs to Valkey, and this project must not
/// reference a cache client. The in-memory implementation shipped here is correct for a single
/// instance; a horizontally scaled deployment needs a shared one, because a token consumed on one
/// instance has to be refused on every other.
/// </remarks>
public interface ITokenReplayGuard
{
    /// <summary>
    /// Records an identifier as consumed, if it was not already.
    /// </summary>
    /// <param name="jti">The token identifier.</param>
    /// <param name="expiresAt">Expiry of the token. The guard only has to remember the identifier
    /// until then: after expiry the token is refused on its own <c>exp</c>, so keeping it longer
    /// buys nothing and costs memory.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the identifier had not been seen and is now consumed;
    /// <see langword="false"/> when it was already used, or when the guard cannot take
    /// responsibility for it. The false case is a refusal, never a silent pass.</returns>
    ValueTask<bool> TryConsumeAsync(string jti, DateTimeOffset expiresAt, CancellationToken ct);
}
