using System.Text.RegularExpressions;

using Dle.Edge.Resolution;

namespace Dle.SecurityTests.Hardening;

/// <summary>
/// S-04 and T-11: the response hardening headers, and an interstitial policy with no
/// <c>unsafe-inline</c> anywhere in it.
/// </summary>
/// <remarks>
/// <para>
/// The interstitial is the one page in the product that renders customer-controlled text: the link's
/// Open Graph title and description, the tenant's branding, the campaign name. §E.2.2 T-11 is stored
/// cross-site scripting through exactly those values, and S-04 makes the content security policy an
/// automated check rather than a review item.
/// </para>
/// <para>
/// <c>unsafe-inline</c> is the specific word because it is the specific mistake. A page that needs an
/// inline stylesheet — and this one does, for the branding colours and the reduced-motion rule — is
/// one shortcut away from a policy that permits every inline script on the page as well. The nonce is
/// the correct answer, and asserting the absence of the shortcut is what stops somebody reaching for
/// it the next time a style refuses to apply.
/// </para>
/// </remarks>
[Collection(EdgeCollection.Name)]
public sealed class SecurityHeaderTests
{
    private const string RedirectSlug = "hdr-redirect";

    private const string InterstitialSlug = "hdr-interstitial";

    private const string GoneSlug = "hdr-gone";

    /// <summary>An in-app webview, which is the client the interstitial exists for (§A.2.6).</summary>
    private const string InAppUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) "
        + "Mobile/15E148 Instagram 340.0.0.24.109";

    private static readonly Regex NoncePattern = new(
        "'nonce-[A-Za-z0-9_-]{22}'",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly EdgeFixture _edge;

    public SecurityHeaderTests(EdgeFixture edge)
    {
        _edge = edge;

        _edge.Factory.Links.Add(
            FakeDomainConfigStore.KnownHost,
            FakeLinkStore.WebLink(RedirectSlug, "https://www.example.com/promo"));

        _edge.Factory.Links.Add(
            FakeDomainConfigStore.KnownHost,
            FakeLinkStore.InterstitialLink(
                InterstitialSlug,
                "https://www.example.com/promo",
                "https://apps.apple.com/app/id123456789?pt=1234&ct=autumn&mt=8"));

        _edge.Factory.Links.Add(
            FakeDomainConfigStore.KnownHost,
            FakeLinkStore.WebLink(GoneSlug, "https://www.example.com/promo") with
            {
                QuarantinedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });

    }

    public static TheoryData<string> EveryOutcome() =>
    [
        "/" + RedirectSlug,
        "/" + InterstitialSlug,
        "/" + GoneSlug,
        "/there-is-no-such-slug",
    ];

    [Theory]
    [MemberData(nameof(EveryOutcome))]
    [Trait("Criterion", "S-04")]
    public async Task EveryResponse_CarriesTheHardeningHeaderSet(string path)
    {
        using HttpResponseMessage response = await GetAsync(path, InAppUserAgent, "198.18.30.1");

        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Single(response, "X-Frame-Options"));
        Assert.Equal(SecurityHeadersMiddleware.ReferrerPolicy, Single(response, "Referrer-Policy"));
        Assert.Equal(SecurityHeadersMiddleware.PermissionsPolicy, Single(response, "Permissions-Policy"));
        Assert.False(string.IsNullOrEmpty(Single(response, "Content-Security-Policy")));
    }

    [Fact]
    [Trait("Criterion", "S-04")]
    public async Task ARequestOverTls_CarriesStrictTransportSecurity()
    {
        // Only over TLS: a development host reached over plain HTTP that also serves an origin over
        // TLS would otherwise pin itself, and un-pinning is a two-year wait.
        using HttpResponseMessage response = await GetAsync("/" + RedirectSlug, InAppUserAgent, "198.18.30.2");

        string hsts = Single(response, "Strict-Transport-Security");

        Assert.Contains("max-age=", hsts, StringComparison.Ordinal);
        Assert.Contains("includeSubDomains", hsts, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Criterion", "S-04")]
    [Trait("Threat", "T-11")]
    public async Task TheInterstitialPolicy_ContainsNoUnsafeDirectiveAnywhere()
    {
        using HttpResponseMessage response = await GetAsync("/" + InterstitialSlug, InAppUserAgent, "198.18.30.3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string policy = Single(response, "Content-Security-Policy");

        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsafe-eval", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsafe-hashes", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("strict-dynamic", policy, StringComparison.OrdinalIgnoreCase);

        // A wildcard script source is the same hole with a different spelling.
        Assert.DoesNotContain("script-src *", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("default-src *", policy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Criterion", "S-04")]
    [Trait("Threat", "T-11")]
    public async Task TheInterstitialPolicy_BindsScriptsAndStylesToAPerResponseNonce()
    {
        using HttpResponseMessage first = await GetAsync("/" + InterstitialSlug, InAppUserAgent, "198.18.30.4");
        using HttpResponseMessage second = await GetAsync("/" + InterstitialSlug, InAppUserAgent, "198.18.30.4");

        string firstPolicy = Single(first, "Content-Security-Policy");
        string secondPolicy = Single(second, "Content-Security-Policy");

        Assert.Contains("script-src 'nonce-", firstPolicy, StringComparison.Ordinal);
        Assert.Contains("style-src", firstPolicy, StringComparison.Ordinal);
        Assert.Contains("'nonce-", firstPolicy, StringComparison.Ordinal);

        // A nonce that is the same on two responses is a constant, and a constant nonce is decoration:
        // an attacker who can read one page can then write a script tag that any later page accepts.
        Assert.NotEqual(firstPolicy, secondPolicy);
        Assert.Equal(NoncePattern.Replace(firstPolicy, "'nonce-X'"), NoncePattern.Replace(secondPolicy, "'nonce-X'"));
    }

    [Fact]
    [Trait("Criterion", "S-04")]
    [Trait("Threat", "T-11")]
    public async Task TheInterstitialDocument_CarriesNoInlineEventHandlerOrJavascriptUrl()
    {
        // The policy is the enforcement; this is the code the policy would have to forgive. An
        // onclick= attribute or a javascript: href in the markup means somebody has already had to
        // weaken the policy once, or is about to.
        using HttpResponseMessage response = await GetAsync("/" + InterstitialSlug, InAppUserAgent, "198.18.30.5");

        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" onclick=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" onload=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" onerror=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Criterion", "S-04")]
    public async Task AResponseWithoutMarkup_CarriesTheStrictestPolicy()
    {
        // A 302 has no document, so nothing needs a source at all. default-src 'none' plus the three
        // directives it does not cover is the strictest thing a response can say, and it costs three
        // hundred bytes on a redirect.
        using HttpResponseMessage response = await GetAsync("/" + RedirectSlug, userAgent: null, "198.18.30.6");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        Assert.Equal(
            SecurityHeadersMiddleware.NonDocumentContentSecurityPolicy,
            Single(response, "Content-Security-Policy"));
    }

    [Fact]
    [Trait("Criterion", "S-04")]
    public async Task AQuarantinedLink_IsGoneRatherThanRedirectedAndStillCarriesThePolicy()
    {
        // TC-103 at the header level: the withdrawn link renders an explanation, and an explanation is
        // markup, so it is one of the pages the policy has to be right for.
        using HttpResponseMessage response = await GetAsync("/" + GoneSlug, InAppUserAgent, "198.18.30.7");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain("unsafe-inline", Single(response, "Content-Security-Policy"), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? userAgent, string address)
    {
        using HttpClient client = _edge.Factory.CreateClientFrom(address);
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));

        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Single(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out IEnumerable<string>? values))
        {
            return string.Join(", ", values);
        }

        return response.Content.Headers.TryGetValues(name, out IEnumerable<string>? contentValues)
            ? string.Join(", ", contentValues)
            : string.Empty;
    }
}
