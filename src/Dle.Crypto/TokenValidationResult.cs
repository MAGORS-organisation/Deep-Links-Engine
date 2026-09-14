namespace Dle.Crypto;

/// <summary>
/// Outcome of validating a token: valid or not, and why not.
/// </summary>
/// <remarks>
/// The reason is for logs and metrics, never for the response body. Telling a caller whether a
/// token failed on its signature, its audience or its expiry turns validation into an oracle;
/// SHARED-KERNEL §17.7 makes the same point about tenant isolation, where a foreign resource is a
/// 404 rather than a 403.
/// </remarks>
public sealed record TokenValidationResult
{
    /// <summary>The token is not in the <c>dlt1</c> format, or a segment is not base64url.</summary>
    public const string ReasonMalformed = "malformed";

    /// <summary>The payload segment is not the JSON this module writes.</summary>
    public const string ReasonMalformedPayload = "malformed_payload";

    /// <summary>The algorithm is not accepted, the key is unknown or retired, or the signature
    /// does not verify. The three are deliberately one reason: distinguishing them would tell an
    /// attacker which part of a forged token to fix next.</summary>
    public const string ReasonSignature = "signature";

    /// <summary>The token was minted for a different audience.</summary>
    public const string ReasonAudience = "audience";

    /// <summary>The token has expired.</summary>
    public const string ReasonExpired = "expired";

    /// <summary>The token claims an issue instant in the future, beyond the allowed skew.</summary>
    public const string ReasonNotYetValid = "not_yet_valid";

    /// <summary>The identifier has already been consumed.</summary>
    public const string ReasonReplayed = "replayed";

    /// <summary>Reason value on a successful validation.</summary>
    public const string ReasonValid = "valid";

    /// <summary>Whether the token is valid.</summary>
    public required bool IsValid { get; init; }

    /// <summary>One of the <c>Reason</c> constants on this type.</summary>
    public required string Reason { get; init; }

    /// <summary>The payload, present only when <see cref="IsValid"/> is
    /// <see langword="true"/>. A payload from a failed validation is never returned, so it cannot
    /// be read by accident.</summary>
    public SignedTokenPayload? Payload { get; init; }

    /// <summary>Builds a failed result.</summary>
    /// <param name="reason">One of the <c>Reason</c> constants.</param>
    /// <returns>The result.</returns>
    public static TokenValidationResult Failure(string reason) => new()
    {
        IsValid = false,
        Reason = reason,
        Payload = null,
    };

    /// <summary>Builds a successful result.</summary>
    /// <param name="payload">The verified payload.</param>
    /// <returns>The result.</returns>
    public static TokenValidationResult Success(SignedTokenPayload payload) => new()
    {
        IsValid = true,
        Reason = ReasonValid,
        Payload = payload,
    };
}
