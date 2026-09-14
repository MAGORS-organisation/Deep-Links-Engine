namespace Dle.Domain.Crypto;

/// <summary>
/// Creates and validates click identifiers. The identifier carries an encrypted timestamp so that
/// the attribution service can narrow the search to the relevant partitions instead of scanning
/// the whole click stream (§B.6.3).
/// </summary>
/// <remarks>
/// Without the embedded timestamp, a lookup by click identifier on a partitioned table has no
/// pruning predicate and degrades into a scan of every partition. The identifier is public: it
/// travels in the Play referrer and in query parameters, so it is authenticated rather than
/// secret, and any tampering has to be detectable (T-05, TC-167).
/// </remarks>
public interface IClickIdCodec
{
    /// <summary>Creates a click identifier for an instant.</summary>
    /// <param name="occurredAt">Instant of the click, in UTC.</param>
    /// <returns>The public click identifier.</returns>
    string New(DateTimeOffset occurredAt);

    /// <summary>Decodes a click identifier.</summary>
    /// <param name="clickId">The identifier as received from a client. Untrusted.</param>
    /// <param name="occurredAt">Instant encoded in the identifier, used to prune partitions.</param>
    /// <param name="sequence">Sequence component of the identifier.</param>
    /// <returns><see langword="false"/> when the identifier was tampered with or is malformed. A
    /// tampered identifier is recorded as such and never matched (T-05, TC-167).</returns>
    bool TryDecode(string clickId, out DateTimeOffset occurredAt, out long sequence);
}
