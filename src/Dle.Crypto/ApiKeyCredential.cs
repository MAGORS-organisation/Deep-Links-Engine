namespace Dle.Crypto;

/// <summary>
/// A freshly minted API key: the value handed to the operator once, and the two columns that go
/// into <c>api_keys</c>.
/// </summary>
/// <remarks>
/// A class rather than a record, because <see cref="Hash"/> is an array and record equality over
/// an array compares references.
/// </remarks>
public sealed class ApiKeyCredential
{
    /// <summary>
    /// The full key, shown to the operator exactly once. Nothing in the engine can recover it
    /// afterwards, which is the point of storing only <see cref="Hash"/>.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>
    /// The non secret prefix, stored in clear text in <c>api_keys.prefix</c>. It exists so that a
    /// presented key can be located with an index lookup instead of hashing every row, and so that
    /// an operator can tell two keys apart in a list without seeing either of them.
    /// </summary>
    public required string Prefix { get; init; }

    /// <summary>
    /// The Argon2id hash of <see cref="Token"/>, as UTF-8 bytes of the PHC encoded form, ready for
    /// the <c>bytea</c> column. The encoded form carries its own cost parameters, so raising the
    /// policy later does not strand existing rows.
    /// </summary>
    public required byte[] Hash { get; init; }
}
