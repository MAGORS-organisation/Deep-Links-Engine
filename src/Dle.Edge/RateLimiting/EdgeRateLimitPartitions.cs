using System.Net;

namespace Dle.Edge.RateLimiting;

/// <summary>
/// Which rate limit table row of §E.9 a request falls under, and the key its budget is counted
/// against.
/// </summary>
/// <remarks>
/// <para>
/// The kind is part of the partition key rather than a separate limiter registration. That is the
/// mechanical guarantee behind the single most important property of the table: the successful-resolve
/// budget and the QR budget count against different partitions even for the same client, so exhausting
/// one can never switch the other off (T-07).
/// </para>
/// <para>
/// Requests that are not in the table at all — health probes, association files, static assets —
/// resolve to <see cref="EdgeRateLimitKind.Unlimited"/>. A probe that a rate limiter can answer with
/// 429 is a probe that takes an instance out of rotation during exactly the traffic spike it was
/// installed to survive.
/// </para>
/// </remarks>
public enum EdgeRateLimitKind
{
    /// <summary>Not covered by any row of §E.9.</summary>
    Unlimited = 0,

    /// <summary><c>GET /{slug}</c>: sliding window, 600 per minute, keyed by network prefix.</summary>
    Resolve = 1,

    /// <summary><c>GET /{slug}/qr</c>: sliding window, 30 per minute, keyed by address.</summary>
    Qr = 2,
}

/// <summary>
/// The partition a request is counted in: what kind of budget, and whose.
/// </summary>
/// <param name="Kind">Which row of §E.9 applies.</param>
/// <param name="Key">The client key, already reduced to a network prefix or an address.</param>
public readonly record struct EdgeRateLimitPartition(EdgeRateLimitKind Kind, string Key);

/// <summary>
/// Reduces a client address to the key its rate limit budget is counted against.
/// </summary>
/// <remarks>
/// <para>
/// §E.9 keys the resolve limits by network prefix — a <c>/24</c> for IPv4 and a <c>/48</c> for IPv6 —
/// and not by address. The reason is arithmetic: a residential IPv6 allocation is a <c>/56</c> or a
/// <c>/64</c>, so an attacker with one line has billions of addresses and, keyed by address, billions
/// of independent budgets. A <c>/48</c> is the smallest block a site is normally allocated, which
/// makes it the level at which the budget means something.
/// </para>
/// <para>
/// The prefix itself is computed by <see cref="IIpHasher.Prefix(IPAddress)"/> rather than here, so
/// that the string a request is rate limited under and the string written to
/// <c>click_events.ip_prefix</c> are produced by one implementation and can never drift apart.
/// </para>
/// </remarks>
public static class EdgeRateLimitPartitions
{
    /// <summary>Key used when the client address is unknown.</summary>
    /// <remarks>
    /// Everything without a usable address shares one bucket. That is deliberately strict: an address
    /// is missing when a proxy is misconfigured or when a request arrives over a transport that has
    /// none, and a shared bucket fails towards refusing rather than towards an unmetered channel.
    /// </remarks>
    public const string UnknownClientKey = "unknown";

    /// <summary>Single key every unlimited request shares, so no partition is created for them.</summary>
    public const string UnlimitedKey = "-";

    /// <summary>Path suffix that identifies the QR endpoint.</summary>
    public const string QrSuffix = "/qr";

    /// <summary>
    /// Returns the network prefix a client is rate limited under.
    /// </summary>
    /// <param name="hasher">The address hasher, which owns the prefix arithmetic.</param>
    /// <param name="address">The client address, or <see langword="null"/> when it is unknown.</param>
    /// <returns>A <c>/24</c> or <c>/48</c> prefix, or <see cref="UnknownClientKey"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hasher"/> is <see langword="null"/>.</exception>
    public static string NetworkKey(IIpHasher hasher, IPAddress? address)
    {
        ArgumentNullException.ThrowIfNull(hasher);

        if (address is null)
        {
            return UnknownClientKey;
        }

        // A proxy on the same host presents ::ffff:10.0.0.7; unwrapping it first keeps one client in
        // one bucket regardless of which socket family the hop in front happened to use.
        IPAddress canonical = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        return hasher.Prefix(canonical) ?? UnknownClientKey;
    }

    /// <summary>
    /// Returns the exact-address key the QR limit is counted under (§E.9 keys that row by IP).
    /// </summary>
    /// <param name="address">The client address, or <see langword="null"/> when it is unknown.</param>
    /// <returns>The address in its canonical text form, or <see cref="UnknownClientKey"/>.</returns>
    public static string AddressKey(IPAddress? address)
    {
        if (address is null)
        {
            return UnknownClientKey;
        }

        IPAddress canonical = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        return canonical.ToString();
    }

    /// <summary>
    /// Decides which row of §E.9 a request falls under from its method and path alone.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The request path.</param>
    /// <returns>The applicable kind.</returns>
    /// <remarks>
    /// Classification is by shape rather than by endpoint metadata so that it holds for the QR
    /// endpoint too, which is mapped by the rendering module. A limit that only applies when another
    /// module remembers to opt in is a limit that is one merge away from not existing.
    /// </remarks>
    public static EdgeRateLimitKind Classify(string method, PathString path)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            return EdgeRateLimitKind.Unlimited;
        }

        string? value = path.Value;

        if (string.IsNullOrEmpty(value) || value.Length < 2 || value[0] != '/')
        {
            return EdgeRateLimitKind.Unlimited;
        }

        ReadOnlySpan<char> trimmed = value.AsSpan(1).TrimEnd('/');

        if (trimmed.IsEmpty)
        {
            return EdgeRateLimitKind.Unlimited;
        }

        int slash = trimmed.IndexOf('/');

        if (slash < 0)
        {
            return IsSlugCandidate(trimmed) ? EdgeRateLimitKind.Resolve : EdgeRateLimitKind.Unlimited;
        }

        // Exactly "{slug}/qr" and nothing deeper.
        ReadOnlySpan<char> head = trimmed[..slash];
        ReadOnlySpan<char> tail = trimmed[slash..];

        if (tail.Equals(QrSuffix, StringComparison.OrdinalIgnoreCase) && IsSlugCandidate(head))
        {
            return EdgeRateLimitKind.Qr;
        }

        return EdgeRateLimitKind.Unlimited;
    }

    /// <summary>
    /// Whether a single path segment could be a slug rather than a reserved word.
    /// </summary>
    /// <remarks>
    /// Only the length and the reserved list are checked. Full normalization belongs to the resolve
    /// handler; doing it here would mean running it twice per request to answer a coarser question.
    /// </remarks>
    private static bool IsSlugCandidate(ReadOnlySpan<char> segment)
    {
        if (segment.Length is 0 or > SlugPolicy.MaxLength)
        {
            return false;
        }

        foreach (string reserved in SlugPolicy.Reserved)
        {
            if (segment.Equals(reserved, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
