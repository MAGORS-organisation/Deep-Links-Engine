using System.Text.Json.Serialization;

namespace Dle.Crypto;

/// <summary>
/// The payload segment of a token in the <c>dlt1</c> format (§E.4.2).
/// </summary>
/// <remarks>
/// The four registered fields are all mandatory, and each earns its place: <c>exp</c> bounds the
/// damage of a leaked token, <c>aud</c> stops a token minted for one endpoint from being replayed
/// against another, <c>jti</c> is what a replay guard keys on, and <c>iat</c> lets a receiver
/// reject a token that claims to come from the future. Anything else the caller needs goes into
/// <see cref="Claims"/>, which keeps the registered set stable while the engine grows.
/// </remarks>
public sealed record SignedTokenPayload
{
    /// <summary>Issue instant, as seconds since the Unix epoch.</summary>
    [JsonPropertyName("iat")]
    public required long Iat { get; init; }

    /// <summary>Expiry instant, as seconds since the Unix epoch.</summary>
    [JsonPropertyName("exp")]
    public required long Exp { get; init; }

    /// <summary>Unique token identifier, the key a replay guard remembers.</summary>
    [JsonPropertyName("jti")]
    public required string Jti { get; init; }

    /// <summary>Intended audience.</summary>
    [JsonPropertyName("aud")]
    public required string Aud { get; init; }

    /// <summary>Application specific claims, or <see langword="null"/> when there are none.</summary>
    [JsonPropertyName("claims")]
    public IReadOnlyDictionary<string, string>? Claims { get; init; }

    /// <summary>Issue instant as a point in time.</summary>
    [JsonIgnore]
    public DateTimeOffset IssuedAt => DateTimeOffset.FromUnixTimeSeconds(Iat);

    /// <summary>Expiry instant as a point in time.</summary>
    [JsonIgnore]
    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds(Exp);
}
