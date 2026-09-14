using System.Net;
using System.Net.Sockets;

using Dle.Domain.Abuse;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// Why a destination URL was refused.
/// </summary>
/// <param name="Accepted">Whether the URL may be registered.</param>
/// <param name="Reason">What is wrong with it, in terms the integrator can act on.</param>
public readonly record struct WebhookDestinationVerdict(bool Accepted, string? Reason)
{
    /// <summary>An accepted destination.</summary>
    public static WebhookDestinationVerdict Ok { get; } = new(true, null);

    /// <summary>Builds a refusal.</summary>
    /// <param name="reason">What is wrong.</param>
    /// <returns>The verdict.</returns>
    public static WebhookDestinationVerdict Reject(string reason) => new(false, reason);
}

/// <summary>
/// Decides whether a webhook may be pointed at a URL (T-02, §E.3 steps 1 and 2).
/// </summary>
/// <remarks>
/// <para>
/// A webhook destination is a URL a customer supplies and the control plane then fetches on a
/// schedule, from inside the deployment's own network. That is a server side request forgery
/// primitive by construction, and the interesting targets are not on the public internet: the
/// cloud metadata endpoint at <c>169.254.169.254</c>, a Kubernetes API server on a cluster
/// address, an internal admin panel that trusts anything arriving from localhost.
/// </para>
/// <para>
/// The judgement is <see cref="TargetUrlPolicy"/>, the same one that decides whether a link may
/// point somewhere. One implementation, so that widening or narrowing what counts as a forbidden
/// range moves both at once — a webhook and a link target are the same problem wearing different
/// names.
/// </para>
/// <para>
/// This check runs when the subscription is created and is deliberately <em>not</em> the last one.
/// Between a resolution here and a connection later, DNS can answer differently; the connect
/// callback in <see cref="Dle.Control.Features.Shared.PublicEndpointGuard"/> validates the address
/// the socket is actually connected to, and that is the check that is proof against rebinding.
/// This one exists so a mistake is reported at creation, with a message, instead of showing up as
/// a stream of failed deliveries.
/// </para>
/// </remarks>
public static class WebhookDestinationPolicy
{
    /// <summary>
    /// Validates a destination URL, resolving it when the policy requires a public address.
    /// </summary>
    /// <param name="url">The URL as supplied.</param>
    /// <param name="allowPrivate">Whether a non-public address is tolerated. Only ever true in a
    /// test harness.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verdict.</returns>
    public static async Task<WebhookDestinationVerdict> ValidateAsync(
        string? url,
        bool allowPrivate,
        CancellationToken cancellationToken)
    {
        UrlSafetyVerdict syntax = TargetUrlPolicy.ValidateSyntax(url);

        if (syntax.Level != UrlSafetyLevel.Safe)
        {
            return WebhookDestinationVerdict.Reject(
                syntax.Reason ?? "The destination must be an absolute http or https URL.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            return WebhookDestinationVerdict.Reject("The destination is not an absolute URL.");
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !allowPrivate)
        {
            // Plain HTTP is refused rather than merely discouraged. The payload carries attribution
            // data about the customer's own users, and a signature proves origin, not confidentiality.
            return WebhookDestinationVerdict.Reject("The destination must use https.");
        }

        if (allowPrivate)
        {
            return WebhookDestinationVerdict.Ok;
        }

        if (TargetUrlPolicy.IsForbiddenHost(parsed.Host))
        {
            return WebhookDestinationVerdict.Reject(
                "The destination host is an internal name and is never a permitted target.");
        }

        IPAddress[] addresses;

        try
        {
            addresses = IPAddress.TryParse(parsed.Host, out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(parsed.Host, cancellationToken);
        }
        catch (SocketException)
        {
            // A name that does not resolve is refused rather than accepted optimistically. Default
            // deny: a destination the engine cannot reach today is a destination that produces a
            // queue of failures tomorrow (SHARED-KERNEL §17.9).
            return WebhookDestinationVerdict.Reject("The destination host could not be resolved.");
        }

        if (addresses.Length == 0)
        {
            return WebhookDestinationVerdict.Reject("The destination host could not be resolved.");
        }

        foreach (IPAddress address in addresses)
        {
            if (TargetUrlPolicy.IsForbiddenAddress(address))
            {
                // Refused if *any* address is forbidden, not only if all of them are. A name that
                // resolves to one public and one link-local address is a name that can be made to
                // resolve to the link-local one at connection time.
                return WebhookDestinationVerdict.Reject(
                    "The destination resolves to an address that is not a permitted target (T-02).");
            }
        }

        return WebhookDestinationVerdict.Ok;
    }

    /// <summary>
    /// Validates the requested event types against the closed set.
    /// </summary>
    /// <param name="eventTypes">The requested types.</param>
    /// <param name="accepted">The normalized, de-duplicated list.</param>
    /// <param name="reason">Why the list was refused.</param>
    /// <returns><see langword="true"/> when every requested type is known.</returns>
    /// <remarks>
    /// An unknown event type is a validation error rather than a no-op subscription. A customer who
    /// subscribes to <c>attribution.create</c> and waits for callbacks that never arrive will blame
    /// the engine, and they will be right to: the engine knew at creation time and said nothing.
    /// </remarks>
    public static bool TryNormalizeEventTypes(
        IReadOnlyList<string>? eventTypes,
        out List<string> accepted,
        out string? reason)
    {
        accepted = [];

        if (eventTypes is null || eventTypes.Count == 0)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"At least one event type is required. Known types: {string.Join(", ", WebhookEventTypes.All)}.");

            return false;
        }

        foreach (string candidate in eventTypes)
        {
            string normalized = candidate?.Trim() ?? string.Empty;

            if (!WebhookEventTypes.IsKnown(normalized))
            {
                // The offending value is not echoed back. It is caller supplied text, and a
                // problem document that repeats it is a reflection point for anything that later
                // renders these documents (SHARED-KERNEL §17.5); naming the closed set is more
                // useful to the integrator anyway.
                reason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"One of the requested event types is not known. Known types: {string.Join(", ", WebhookEventTypes.All)}.");

                accepted = [];
                return false;
            }

            if (!accepted.Contains(normalized, StringComparer.Ordinal))
            {
                accepted.Add(normalized);
            }
        }

        reason = null;
        return true;
    }
}
