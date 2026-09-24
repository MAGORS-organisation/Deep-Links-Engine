using Dle.Domain.Clients;

namespace Dle.Domain.Ports;

/// <summary>
/// Classifies an incoming request into the client context that drives both routing and the shape
/// of the response (ADR-009).
/// </summary>
/// <remarks>
/// The distinction between a crawler, an in-app web view and an ordinary browser is not cosmetic:
/// a crawler needs HTML with Open Graph tags rather than a redirect, and an in-app web view needs
/// an interstitial with a real anchor to tap, because neither Facebook nor Instagram nor TikTok
/// triggers OS level link capture on page load (§A.2.6).
/// </remarks>
public interface IClientClassifier
{
    /// <summary>Classifies a request.</summary>
    /// <param name="request">The transport neutral request.</param>
    /// <returns>The classified context. Classification never fails: an unrecognised client is
    /// reported as unknown and routed by the default rule.</returns>
    ClientContext Classify(ClientRequest request);
}
