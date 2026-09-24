using System.Collections.ObjectModel;
using System.Text;

using Dle.Edge.Rendering;

namespace Dle.SecurityTests.OpenRedirect;

/// <summary>
/// T-01 / CWE-601 / TC-164: no request-supplied value may ever steer a redirect.
/// </summary>
/// <remarks>
/// <para>
/// A link engine is a redirector, and §E.1 is blunt about what that means: "každý redirektor je v
/// podstate stroj na obchádzanie reputačných filtrov". The whole value of the domain is that a
/// recipient trusts it, and one query parameter that reaches the <c>Location</c> header spends that
/// trust on whoever found it.
/// </para>
/// <para>
/// The defence in this codebase is structural rather than filtering:
/// <see cref="RoutingUrlBuilder.ForwardableQueryKeys"/> is an allow list, request parameters are only
/// ever appended to the <em>query</em> of a target whose scheme, host and path came from a stored
/// rule, and <see cref="Dle.Edge.Rendering.SafeUrl"/> checks the finished URL a second time. These
/// tests are what makes that claim falsifiable: a corpus, generated across four axes, pushed through
/// every parameter name a scanner tries, with one assertion — the client is never sent anywhere the
/// operator did not configure.
/// </para>
/// </remarks>
[Collection(EdgeCollection.Name)]
public sealed class OpenRedirectTests
{
    /// <summary>The only destination the seeded link is allowed to produce.</summary>
    private const string TargetHost = "www.example.com";

    private const string TargetUrl = "https://" + TargetHost + "/promo/autumn";

    // Lower case: SlugPolicy.TryNormalize case-folds before the lookup, so this is the key the
    // store is actually asked for.
    private const string Slug = "ab3xk9pq";

    /// <summary>Hosts a redirect may legitimately name in these tests.</summary>
    private static readonly string[] PermittedHosts = [TargetHost, FakeDomainConfigStore.KnownHost];

    private readonly EdgeFixture _edge;

    public OpenRedirectTests(EdgeFixture edge)
    {
        _edge = edge;
        _edge.Factory.Links.Add(FakeDomainConfigStore.KnownHost, FakeLinkStore.WebLink(Slug, TargetUrl));
    }

    /// <summary>The sample driven through the HTTP pipeline.</summary>
    public static TheoryData<string> SampledCorpus()
    {
        TheoryData<string> data = new();

        foreach (string value in HostileUrlCorpus.Sample(150))
        {
            data.Add(value);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SampledCorpus))]
    [Trait("TestCase", "TC-164")]
    [Trait("Threat", "T-01")]
    public async Task Resolve_HostileValueInASteeringParameter_NeverReachesTheLocationHeader(string hostile)
    {
        using HttpClient client = _edge.Factory.CreateClientFrom("203.0.113.10");

        foreach (string parameter in HostileUrlCorpus.SteeringParameterNames)
        {
            string url = "/" + Slug + "?" + parameter + "=" + Uri.EscapeDataString(hostile);

            using HttpResponseMessage response = await client.GetAsync(
                new Uri(url, UriKind.Relative),
                TestContext.Current.CancellationToken);

            AssertSafeRedirect(response, parameter, hostile);
        }
    }

    [Theory]
    [MemberData(nameof(SampledCorpus))]
    [Trait("TestCase", "TC-164")]
    [Trait("Threat", "T-01")]
    public async Task Resolve_HostileValueInAForwardableParameter_IsCarriedAsDataNotAsADestination(string hostile)
    {
        // The allow-listed parameters are the interesting ones. They *are* copied into the target, so
        // the property is narrower and more delicate: the value may appear in the query string of the
        // configured target, and must never change its scheme, host or path.
        using HttpClient client = _edge.Factory.CreateClientFrom("198.51.100.11");

        foreach (string parameter in RoutingUrlBuilder.ForwardableQueryKeys)
        {
            string url = "/" + Slug + "?" + parameter + "=" + Uri.EscapeDataString(hostile);

            using HttpResponseMessage response = await client.GetAsync(
                new Uri(url, UriKind.Relative),
                TestContext.Current.CancellationToken);

            AssertSafeRedirect(response, parameter, hostile, allowMarkerInQuery: true);
        }
    }

    [Theory]
    [MemberData(nameof(SampledCorpus))]
    [Trait("TestCase", "TC-164")]
    [Trait("Threat", "T-01")]
    public async Task Resolve_HostileValueAsTheSlug_IsNeverEchoedIntoALocationHeader(string hostile)
    {
        // The slug is the other request-controlled input. Most of the corpus cannot even route to the
        // resolve endpoint because it carries a separator, and that is a valid outcome: what is not
        // valid is any of it turning into a destination.
        using HttpClient client = _edge.Factory.CreateClientFrom("192.0.2.12");

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/" + Uri.EscapeDataString(hostile), UriKind.Relative),
            TestContext.Current.CancellationToken);

        AssertSafeRedirect(response, "slug", hostile);
    }

    [Theory]
    [MemberData(nameof(SampledCorpus))]
    [Trait("TestCase", "TC-164")]
    [Trait("Threat", "T-01")]
    public async Task Resolve_HostileValueInTheReferrer_DoesNotInfluenceTheDestination(string hostile)
    {
        // The referrer is attacker controlled on any page that links here, and §E.6.3 already limits
        // what may be stored from it. It must not reach the response either.
        using HttpClient client = _edge.Factory.CreateClientFrom("198.18.0.13");

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri("/" + Slug, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Referer", "https://" + hostile);

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        AssertSafeRedirect(response, "referer", hostile);
    }

    [Fact]
    [Trait("TestCase", "TC-164")]
    public async Task Resolve_WithNoHostileInput_StillRedirectsToTheConfiguredTarget()
    {
        // The control. Everything above would also pass against an engine that answered 500 to every
        // request, so the happy path is asserted in the same class rather than in a different suite.
        using HttpClient client = _edge.Factory.CreateClientFrom("198.18.1.14");

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/" + Slug + "?utm_source=fb", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        Uri location = new(response.Headers.Location!.ToString(), UriKind.Absolute);

        Assert.Equal(TargetHost, location.Host);
        Assert.Equal("/promo/autumn", location.AbsolutePath);
        Assert.Contains("utm_source=fb", location.Query, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestCase", "TC-164")]
    [Trait("Threat", "T-01")]
    public void UrlBuilder_AcrossTheWholeCorpus_NeverProducesAnAttackerControlledTarget()
    {
        // The same property one layer down, over the entire cross product rather than a sample. This is
        // where the corpus earns its size: a method call costs nothing, so every one of the tens of
        // thousands of compositions is exercised against the component that actually assembles the URL.
        LinkSnapshot link = FakeLinkStore.WebLink(Slug, TargetUrl);
        RoutingDecision decision = new RoutingEngine(TimeProvider.System).Evaluate(
            link.RoutingRules,
            Browser("203.0.113.15"),
            ConsentDecision.Denied("test"),
            "cid00001");

        List<string> failures = [];

        foreach (string hostile in HostileUrlCorpus.Values)
        {
            foreach (string parameter in RoutingUrlBuilder.ForwardableQueryKeys)
            {
                ClientContext client = Browser("203.0.113.15") with
                {
                    Query = new ReadOnlyDictionary<string, string>(
                        new Dictionary<string, string>(StringComparer.Ordinal) { [parameter] = hostile }),
                };

                string built = RoutingUrlBuilder.BuildWebUrl(decision, link, client, "cid00001", ConsentDecision.Denied("test"));

                if (!HostileUrlCorpus.IsSafeLocation(built, PermittedHosts, out string reason, allowMarkerInQuery: true))
                {
                    failures.Add(parameter + "=" + hostile + " -> " + built + " (" + reason + ")");
                }
            }

            if (failures.Count >= 20)
            {
                break;
            }
        }

        Assert.True(
            failures.Count == 0,
            "The URL builder produced an attacker controlled destination:" + Environment.NewLine
                + string.Join(Environment.NewLine, failures.Select(failure => "  " + failure)));
    }

    [Fact]
    [Trait("Threat", "T-01")]
    public void SafeUrl_AcrossTheWholeCorpus_NeverProducesAnythingButAnAbsoluteHttpUrl()
    {
        // SafeUrl is the last gate before a value reaches a Location header or an href, and it is a
        // scheme and shape gate rather than a host gate: an operator is entitled to point a link at any
        // public host, so a hostile *stored* target is E.3's problem and not this one. What SafeUrl must
        // never do is hand on something that is not an absolute http or https URL - a protocol-relative
        // value, a javascript: value, or anything carrying a control character.
        List<string> accepted = [];

        foreach (string hostile in HostileUrlCorpus.Values)
        {
            string? passed = SafeUrl.Web(hostile);

            if (passed is null)
            {
                continue;
            }

            bool wellFormed =
                Uri.TryCreate(passed, UriKind.Absolute, out Uri? parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                && !passed.Any(char.IsControl);

            if (!wellFormed)
            {
                accepted.Add(Describe(hostile) + " -> " + Describe(passed));
            }

            if (accepted.Count >= 20)
            {
                break;
            }
        }

        Assert.True(
            accepted.Count == 0,
            "SafeUrl.Web passed a value that is not an absolute http(s) URL:" + Environment.NewLine
                + string.Join(Environment.NewLine, accepted.Select(value => "  " + value)));
    }

    [Fact]
    [Trait("Threat", "T-01")]
    [Trait("TestCase", "TC-161")]
    public void SafeUrl_WithNoCustomSchemeConfigured_RefusesEveryNonWebScheme()
    {
        // The deep link slot is the one place a non-web scheme is ever allowed, and only when the link
        // declares it. With none declared the allow list must be exactly http and https - a deny list of
        // the schemes known today is a promise about the schemes invented tomorrow.
        List<string> accepted = [];

        foreach (string hostile in HostileUrlCorpus.Values)
        {
            string? passed = SafeUrl.Deeplink(hostile, ReadOnlySpan<string?>.Empty);

            if (passed is null)
            {
                continue;
            }

            if (!Uri.TryCreate(passed, UriKind.Absolute, out Uri? parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                accepted.Add(Describe(hostile));
            }

            if (accepted.Count >= 20)
            {
                break;
            }
        }

        Assert.True(
            accepted.Count == 0,
            "SafeUrl.Deeplink passed a non-web scheme with no custom scheme configured:" + Environment.NewLine
                + string.Join(Environment.NewLine, accepted.Select(value => "  " + value)));
    }

    private static void AssertSafeRedirect(
        HttpResponseMessage response,
        string source,
        string hostile,
        bool allowMarkerInQuery = false)
    {
        string? location = response.Headers.Location?.OriginalString;

        Assert.True(
            HostileUrlCorpus.IsSafeLocation(location, PermittedHosts, out string reason, allowMarkerInQuery),
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 An open redirect was produced through '{source}'.
                   input:    {Describe(hostile)}
                   status:   {(int)response.StatusCode}
                   location: {location ?? "(none)"}
                   reason:   {reason}
                 """));

        // A redirect is the only response that may carry a Location, and no response may carry a second
        // one — a split header is the successful half of a response splitting attack.
        Assert.True(
            !response.Headers.TryGetValues("Location", out IEnumerable<string>? all) || all.Count() == 1,
            "The response carried more than one Location header, which means a header was injected.");

        Assert.False(
            response.Headers.Contains("X-Injected"),
            "A header from the payload appeared in the response: the value reached the header block.");
    }

    private static string Describe(string value)
    {
        StringBuilder builder = new(value.Length + 8);

        foreach (char c in value)
        {
            builder.Append(c switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => char.IsControl(c)
                    ? "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)
                    : c.ToString(),
            });
        }

        return builder.ToString();
    }

    private static ClientContext Browser(string address) => ClientContext.Empty with
    {
        Platform = Platform.Desktop,
        Channel = ClientChannel.Browser,
        DeviceClass = DeviceClass.Desktop,
        RemoteIp = IPAddress.Parse(address),
    };
}
