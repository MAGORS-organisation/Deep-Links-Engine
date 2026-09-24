using System.Collections.ObjectModel;
using System.Net;

namespace Dle.Edge.Resolution;

/// <summary>
/// Maps an ASP.NET Core <see cref="HttpContext"/> onto the transport-neutral
/// <see cref="ClientRequest"/> the shared kernel classifies (SHARED-KERNEL §3).
/// </summary>
/// <remarks>
/// <para>
/// The indirection exists because <c>Dle.Domain</c> may not reference ASP.NET Core, and it earns its
/// place beyond that: it is the one point where the edge decides which parts of an untrusted request
/// are allowed further into the system, and everything downstream sees only what survived.
/// </para>
/// <para>
/// The query string is the part that matters. Only the parameters on
/// <see cref="RoutingUrlBuilder.ForwardableQueryKeys"/> are copied, so a parameter a client invents
/// cannot ride along into the redirect target, and no parameter that steers behaviour — the preview
/// switch, the consent signals — is copied at all, so none of them can leak into an outbound URL. It
/// is also the structural half of SHARED-KERNEL §17.4 and TC-164: no redirect target can be taken from
/// a request parameter if no request parameter capable of being one ever reaches the URL builder.
/// </para>
/// </remarks>
public static class ClientRequestFactory
{
    /// <summary>
    /// Longest forwarded parameter value that is copied. Anything longer is dropped.
    /// </summary>
    /// <remarks>
    /// A click identifier from an advertising platform is tens of characters. A kilobyte of it is
    /// somebody probing what the target URL builder does with a long value, and the target's own
    /// server is entitled not to find out.
    /// </remarks>
    public const int MaxForwardedValueLength = 512;

    /// <summary>Header carrying the client's declared fetch site.</summary>
    public const string SecFetchSiteHeader = "Sec-Fetch-Site";

    /// <summary>Header carrying the client's declared fetch mode.</summary>
    public const string SecFetchModeHeader = "Sec-Fetch-Mode";

    /// <summary>Header carrying the client's declared fetch destination.</summary>
    public const string SecFetchDestHeader = "Sec-Fetch-Dest";

    /// <summary>
    /// Builds the request description the classifier consumes.
    /// </summary>
    /// <param name="context">The incoming request.</param>
    /// <param name="host">The already normalized host.</param>
    /// <param name="receivedAt">When the request arrived, from <see cref="TimeProvider"/>.</param>
    /// <returns>The request description.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static ClientRequest Create(HttpContext context, string host, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpRequest request = context.Request;

        return new ClientRequest
        {
            Host = host,
            Path = request.Path.Value ?? "/",
            UserAgent = First(request.Headers.UserAgent),
            AcceptLanguage = First(request.Headers.AcceptLanguage),
            Referrer = First(request.Headers.Referer),
            RemoteIp = Canonical(context.Connection.RemoteIpAddress),
            SecFetchSite = First(request.Headers[SecFetchSiteHeader]),
            SecFetchMode = First(request.Headers[SecFetchModeHeader]),
            SecFetchDest = First(request.Headers[SecFetchDestHeader]),
            Query = ForwardableQuery(request.Query),
            ReceivedAt = receivedAt,
        };
    }

    /// <summary>
    /// Unwraps an IPv4-mapped IPv6 address so that one client is one address everywhere it is used.
    /// </summary>
    /// <param name="address">The address the connection reported.</param>
    /// <returns>The canonical form, or <see langword="null"/>.</returns>
    public static IPAddress? Canonical(IPAddress? address) =>
        address is not null && address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// Copies the parameters that are allowed to travel to the target (§E.6.3, FR-128).
    /// </summary>
    /// <remarks>
    /// The allowlist is walked, not the request. Walking the request would make the cost of building
    /// this dictionary proportional to how many parameters a client chose to send, which is a cheap
    /// way for it to make every one of its requests expensive; walking the fourteen known keys makes
    /// the cost constant and the result bounded by construction.
    /// </remarks>
    private static ReadOnlyDictionary<string, string> ForwardableQuery(IQueryCollection query)
    {
        if (query.Count == 0)
        {
            return ReadOnlyDictionary<string, string>.Empty;
        }

        Dictionary<string, string>? forwarded = null;

        foreach (string key in RoutingUrlBuilder.ForwardableQueryKeys)
        {
            if (!query.TryGetValue(key, out Microsoft.Extensions.Primitives.StringValues values))
            {
                continue;
            }

            string? value = First(values);

            if (string.IsNullOrEmpty(value) || value.Length > MaxForwardedValueLength)
            {
                continue;
            }

            forwarded ??= new Dictionary<string, string>(StringComparer.Ordinal);
            forwarded[key] = value;
        }

        return forwarded is null
            ? ReadOnlyDictionary<string, string>.Empty
            : new ReadOnlyDictionary<string, string>(forwarded);
    }

    /// <summary>
    /// Returns the first value of a header, or <see langword="null"/> when it was not sent.
    /// </summary>
    /// <remarks>
    /// A repeated header is a signal in itself — a well-behaved client sends one user agent — and
    /// concatenating the values would produce a string no parser was written for. The first wins and
    /// the rest are ignored.
    /// </remarks>
    private static string? First(Microsoft.Extensions.Primitives.StringValues values) =>
        values.Count == 0 ? null : values[0];
}
