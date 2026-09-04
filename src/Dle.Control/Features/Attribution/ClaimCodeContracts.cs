namespace Dle.Control.Features.Attribution;

/// <summary>
/// Body of <c>POST /v1/claim-codes</c>: the interstitial page asking for the six characters it will
/// show the user (S3, FR-184, §B.6.3).
/// </summary>
/// <remarks>
/// The interstitial is served by the edge, which has just written the click event and therefore
/// holds both values below. It exchanges them for a code because the code is the only deferred
/// channel iOS leaves that does not need the user to be signed in.
/// </remarks>
public sealed record IssueClaimCodeRequest
{
    /// <summary>Identifier of the click the code will stand for.</summary>
    public required string ClickId { get; init; }

    /// <summary>Link the click belongs to, as a string because the value is a 64 bit identifier.</summary>
    public string? LinkId { get; init; }
}

/// <summary>
/// A freshly issued claim code. The plaintext code appears here and nowhere else: storage keeps
/// only its keyed hash (§E.4.1, K3).
/// </summary>
public sealed record ClaimCodeResponse
{
    /// <summary>The code to display, six characters from the homoglyph-free alphabet.</summary>
    public required string Code { get; init; }

    /// <summary>When the code stops being redeemable, in UTC.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Seconds until <see cref="ExpiresAt"/>, so the page can count down without a clock
    /// of its own.</summary>
    public required int ExpiresIn { get; init; }
}
