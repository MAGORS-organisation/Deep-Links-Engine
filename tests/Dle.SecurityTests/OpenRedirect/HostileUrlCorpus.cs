using System.Text;

namespace Dle.SecurityTests.OpenRedirect;

/// <summary>
/// Generates the open-redirect corpus (T-01, CWE-601, TC-164).
/// </summary>
/// <remarks>
/// <para>
/// The corpus is composed rather than listed. Five hand-picked strings test five bugs; a cross product
/// of separators, host spellings, encodings and trailers tests the class of bug, and it keeps testing
/// it when somebody adds a normalization step that happens to handle the five. Every element is built
/// from four independent axes:
/// </para>
/// <list type="number">
///   <item><description>a <b>prefix</b> — the separator that makes a parser see an authority where the
///   application intended a path: <c>//</c>, <c>/\</c>, <c>\/</c>, <c>///</c>, <c>http:/\</c>, a tab or
///   a newline between the slashes, and their percent-encoded forms;</description></item>
///   <item><description>a <b>host</b> — the attacker's name in every spelling that has ever survived a
///   check: bare, with a port, upper case, fully qualified with a trailing dot, as an address literal,
///   as an IPv6 literal, in decimal and octal integer form, with a Cyrillic homoglyph, and with an
///   ideographic full stop that browsers normalize to a dot;</description></item>
///   <item><description>a <b>userinfo</b> trick — <c>trusted.example@evil</c> and its encoded
///   variants, where everything before the at sign is credentials and the real host is what
///   follows;</description></item>
///   <item><description>a <b>trailer</b> — a path, a query, a fragment, and a CRLF sequence that would
///   split the response header if the value reached one unescaped.</description></item>
/// </list>
/// <para>
/// The attacker's names all contain the marker <see cref="AttackerMarker"/>, which is what makes the
/// assertion simple and total: whatever the engine answers, that string must not appear anywhere in a
/// <c>Location</c> header — with one deliberate exception, described on
/// <see cref="IsSafeLocation(string?, IReadOnlyCollection{string}, out string, bool)"/>, for the
/// allow-listed UTM parameters that §E.6.3 copies into the target's query as data.
/// </para>
/// </remarks>
public static class HostileUrlCorpus
{
    /// <summary>Every attacker host in the corpus contains this token.</summary>
    public const string AttackerMarker = "evil";

    /// <summary>A host the engine legitimately serves, used in the userinfo tricks.</summary>
    public const string TrustedHost = "link.example.test";

    /// <summary>Separators that can make a value parse as an authority instead of a path.</summary>
    private static readonly string[] Prefixes =
    [
        "//",
        "///",
        "////",
        @"/\",
        @"\/",
        @"\\",
        @"/\/",
        "/%2f",
        "%2f%2f",
        "%2F%2F",
        "%252f%252f",
        "%5c%5c",
        "%5C%5C",
        "/\t/",
        "/\n/",
        "/\r\n/",
        "/ /",
        "/%09/",
        "/%0a/",
        "/%0d%0a/",
        "http://",
        "https://",
        @"http:/\",
        @"https:/\",
        @"https:\\",
        "https:/",
        "HtTpS://",
        "//:@",
        "/..//",
        "/./\\",
        "javascript://",
        "data://",
        "//%0d%0a",
    ];

    /// <summary>Spellings of the attacker's host.</summary>
    private static readonly string[] Hosts =
    [
        "evil.com",
        "evil.com:80",
        "evil.com:443",
        "EVIL.COM",
        "evil.com.",
        "www.evil.com",
        "evil.com%2f",
        "evil.com%23",
        "evil.com%3f",
        "evil%2ecom",
        "evil%252ecom",
        "evıl.com",
        "еvil.com",
        "evil。com",
        "evil．com",
        "xn--evil-9na.com",
        "evil.com#@" + TrustedHost,
        "evil.com?@" + TrustedHost,
        "evil.com\\@" + TrustedHost,
    ];

    /// <summary>Credential prefixes that hide the real host behind a trusted looking one.</summary>
    private static readonly string[] UserInfos =
    [
        string.Empty,
        TrustedHost + "@",
        TrustedHost + ":pass@",
        TrustedHost + "%40",
        TrustedHost + "%2540",
        TrustedHost + "%2f@",
        "user%00@",
        "user%09@",
    ];

    /// <summary>What follows the host.</summary>
    private static readonly string[] Trailers =
    [
        string.Empty,
        "/",
        "/login",
        "/?next=/",
        "#fragment",
        "%23fragment",
        "/%0d%0aX-Injected:%201",
        "/\r\nX-Injected: 1",
        "?utm_source=fb",
    ];

    /// <summary>Query parameter names an attacker would try to steer a redirect with.</summary>
    /// <remarks>
    /// <c>to</c> is the one TC-164 names. The rest are the conventional spellings a generic scanner
    /// tries, and none of them is on <see cref="RoutingUrlBuilder.ForwardableQueryKeys"/>, which is
    /// exactly the property under test.
    /// </remarks>
    public static IReadOnlyList<string> SteeringParameterNames { get; } =
    [
        "to", "url", "u", "r", "redirect", "redirect_uri", "redirect_url", "next", "return",
        "returnUrl", "return_to", "dest", "destination", "continue", "target", "goto", "out",
        "link", "forward", "callback", "image_url", "data", "path", "domain", "host",
    ];

    /// <summary>
    /// Every hostile value, composed from the four axes.
    /// </summary>
    /// <remarks>
    /// Deduplicated and ordered so that a failure names the same case on every machine and every run:
    /// a security regression that reproduces only sometimes gets closed as flaky.
    /// </remarks>
    public static IReadOnlyList<string> Values { get; } = Compose();

    /// <summary>
    /// Takes an evenly spread, deterministic sample of the corpus.
    /// </summary>
    /// <param name="count">How many values to take.</param>
    /// <returns>The sample, in corpus order.</returns>
    /// <remarks>
    /// <para>
    /// The full cross product is tens of thousands of strings. That is the right size for the
    /// in-process assertions, which cost a method call each, and the wrong size for the ones that go
    /// through the HTTP pipeline, which cost a request each and would turn a security suite into
    /// something people skip.
    /// </para>
    /// <para>
    /// The sample is a fixed stride rather than a random draw, so it covers every axis — the corpus is
    /// ordered, and consecutive entries differ in the trailer while distant ones differ in the prefix —
    /// and it is the same sample on every machine. A security test that examines a different subset on
    /// every run is a test whose failures cannot be reproduced.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Sample(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        if (count >= Values.Count)
        {
            return Values;
        }

        // A stride coprime with the corpus size walks the whole list before repeating, so the sample is
        // spread across every axis instead of clustering at the start.
        int stride = Math.Max(1, Values.Count / count);

        while (stride > 1 && Values.Count % stride == 0)
        {
            stride--;
        }

        List<string> sample = new(count);

        for (int i = 0, index = 0; i < count; i++, index = (index + stride) % Values.Count)
        {
            sample.Add(Values[index]);
        }

        return sample;
    }

    /// <summary>A handful of values that are legitimate, as a control group.</summary>
    /// <remarks>
    /// Without these the suite would pass just as happily against an engine that refused every request,
    /// which is not the property anybody wants.
    /// </remarks>
    public static IReadOnlyList<string> BenignValues { get; } =
    [
        "fb",
        "autumn-2026",
        "utm_source=fb",
        "/relative/path",
        "https://" + TrustedHost + "/promo",
        "hello world",
        "a" + new string('b', 400),
    ];

    /// <summary>
    /// Decides whether a <c>Location</c> header is safe.
    /// </summary>
    /// <param name="location">The header value, or <see langword="null"/> when there was none.</param>
    /// <param name="expectedHosts">Hosts the engine is allowed to send a client to.</param>
    /// <param name="reason">Why the header is unsafe.</param>
    /// <param name="allowMarkerInQuery">
    /// Whether the attacker's marker may appear after the path. It may exactly once: an allow-listed
    /// UTM parameter is <em>copied</em> into the target's query by design (§E.6.3), so for those
    /// parameters the property is not "the string never appears" but "it never appears anywhere that
    /// decides where the client goes" - the scheme, the authority or the path.
    /// </param>
    /// <returns><see langword="true"/> when the header names a permitted destination.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expectedHosts"/> is <see langword="null"/>.</exception>
    public static bool IsSafeLocation(
        string? location,
        IReadOnlyCollection<string> expectedHosts,
        out string reason,
        bool allowMarkerInQuery = false)
    {
        ArgumentNullException.ThrowIfNull(expectedHosts);

        if (location is null)
        {
            reason = string.Empty;
            return true;
        }

        foreach (char c in location)
        {
            if (c is '\r' or '\n' || char.IsControl(c))
            {
                reason = "the header carries a control character, which is a response splitting primitive.";
                return false;
            }
        }

        if (!allowMarkerInQuery && location.Contains(AttackerMarker, StringComparison.OrdinalIgnoreCase))
        {
            reason = "the header mentions the attacker's host.";
            return false;
        }

        if (!Uri.TryCreate(location, UriKind.Absolute, out Uri? absolute))
        {
            // A relative Location stays on this origin, unless it is protocol relative — which is the
            // single most common CWE-601 bug and is why the backslash forms are in the corpus.
            bool protocolRelative =
                location.StartsWith("//", StringComparison.Ordinal)
                || location.StartsWith(@"/\", StringComparison.Ordinal)
                || location.StartsWith(@"\\", StringComparison.Ordinal)
                || location.StartsWith(@"\/", StringComparison.Ordinal);

            reason = protocolRelative ? "the header is protocol relative and leaves this origin." : string.Empty;

            return !protocolRelative;
        }

        if (!string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(absolute.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            reason = "the header names the scheme " + absolute.Scheme + ", which a browser may execute.";
            return false;
        }

        if (!string.IsNullOrEmpty(absolute.UserInfo))
        {
            reason = "the header embeds credentials, which disguise the real host.";
            return false;
        }

        if (!expectedHosts.Contains(absolute.Host, StringComparer.OrdinalIgnoreCase))
        {
            reason = "the header names the host '" + absolute.Host + "', which the operator never configured.";
            return false;
        }

        // Everything up to and including the path is what decides where the client goes. The marker may
        // ride along in the query as data; it may never appear before it.
        if (allowMarkerInQuery
            && absolute.GetLeftPart(UriPartial.Path).Contains(AttackerMarker, StringComparison.OrdinalIgnoreCase))
        {
            reason = "the attacker's host reached the scheme, authority or path of the destination.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static string[] Compose()
    {
        HashSet<string> values = new(StringComparer.Ordinal);

        foreach (string prefix in Prefixes)
        {
            foreach (string userInfo in UserInfos)
            {
                foreach (string host in Hosts)
                {
                    foreach (string trailer in Trailers)
                    {
                        values.Add(prefix + userInfo + host + trailer);
                    }
                }
            }
        }

        // The two shapes that are not a prefix/host composition: a bare scheme with no authority, and
        // the encoded whole-URL form a naive decoder turns back into one of the above.
        foreach (string host in Hosts)
        {
            values.Add("https%3A%2F%2F" + host);
            values.Add("https%253A%252F%252F" + host);
            values.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes("https://" + host)));
        }

        return [.. values.Order(StringComparer.Ordinal)];
    }
}
