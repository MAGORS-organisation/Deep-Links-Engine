namespace Dle.Domain.Clients;

/// <summary>
/// Transport neutral description of an incoming request. The edge maps
/// <c>HttpContext</c> onto this record before anything in the domain sees it.
/// </summary>
/// <remarks>
/// The indirection exists because <c>Dle.Domain</c> must not depend on ASP.NET Core: classification
/// and routing are pure functions of this record and are therefore testable without a web host.
/// </remarks>
public sealed record ClientRequest
{
    /// <summary>Raw host from the request. Normalise it with <c>HostNormalizer</c> before any lookup.</summary>
    public required string Host { get; init; }

    /// <summary>Request path, beginning with <c>'/'</c>.</summary>
    public required string Path { get; init; }

    /// <summary>The <c>User-Agent</c> header, if any. Only parsed families are ever stored.</summary>
    public string? UserAgent { get; init; }

    /// <summary>The <c>Accept-Language</c> header, if any.</summary>
    public string? AcceptLanguage { get; init; }

    /// <summary>The <c>Referer</c> header, if any. Only the host is ever stored.</summary>
    public string? Referrer { get; init; }

    /// <summary>Remote address, or <see langword="null"/> when the proxy chain did not yield a usable one.</summary>
    public System.Net.IPAddress? RemoteIp { get; init; }

    /// <summary>The <c>Sec-Fetch-Site</c> header; distinguishes a real navigation from a prefetch.</summary>
    public string? SecFetchSite { get; init; }

    /// <summary>The <c>Sec-Fetch-Mode</c> header.</summary>
    public string? SecFetchMode { get; init; }

    /// <summary>The <c>Sec-Fetch-Dest</c> header.</summary>
    public string? SecFetchDest { get; init; }

    /// <summary>Query string parameters. Keys keep the casing they arrived with.</summary>
    public IReadOnlyDictionary<string, string> Query { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Request headers the edge chose to forward, keyed case insensitively by the caller.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Instant the request was received, in UTC, taken from the ambient <see cref="TimeProvider"/>.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }
}
