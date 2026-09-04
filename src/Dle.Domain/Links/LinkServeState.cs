namespace Dle.Domain.Links;

/// <summary>
/// Why a link may or may not be served right now.
/// </summary>
/// <remarks>
/// The distinction between <see cref="NotFound"/> and <see cref="Gone"/> is deliberate and is covered by
/// TC-103: a withdrawn link answers <c>410</c> with an explanation, while everything else — a missing
/// slug, a deactivated link, or a link belonging to another tenant — answers an indistinguishable
/// <c>404</c> so that no enumeration oracle exists (TC-102, TC-166).
/// </remarks>
public enum LinkServeState
{
    /// <summary>The link may be resolved and routed.</summary>
    Servable = 0,

    /// <summary>The link is not available and must be answered exactly like a non-existent slug.</summary>
    NotFound = 1,

    /// <summary>The link was withdrawn, typically by abuse quarantine. Answered with <c>410</c> (TC-103).</summary>
    Gone = 2,

    /// <summary>The link's validity window has ended. The edge redirects to the configured expiry URL (TC-104).</summary>
    Expired = 3,

    /// <summary>The link's validity window has not started yet.</summary>
    NotYetActive = 4,
}
