using Microsoft.Extensions.Logging;

namespace Dle.SecurityTests.Hardening;

/// <summary>
/// S-08 / T-08: nothing at <c>Information</c> or below may carry personal data.
/// </summary>
/// <remarks>
/// <para>
/// §E.2.2 T-08 names the three values precisely — the <c>Authorization</c> header, the referrer and
/// the query string — and calls the outcome of logging them a GDPR incident rather than a bug. §E.6.3
/// adds the address: what may be stored of it is a hash and a prefix, never the value.
/// </para>
/// <para>
/// This asserts against a capture from the running host rather than against the call sites, because
/// the risk is not only the logging this repository wrote. It is also the logging it inherits: a
/// framework component that logs the request line at <c>Information</c> puts the whole query string
/// into the log without anybody in this codebase writing a statement. The host loads
/// <c>src/Dle.Edge/appsettings.json</c> from its content root exactly as a deployment does, so the
/// framework categories keep the levels that file gives them and the capture is of what those
/// settings actually emit.
/// </para>
/// <para>
/// Scope is <c>Information</c> and below, which is what S-08 says — and "below" is the half that
/// matters. The resolve path logs at <c>Debug</c>, so a host left at the default level emits nothing
/// from it at all and would pass this class vacuously. The host here therefore runs its own
/// categories at <c>Trace</c>, which is the state an operator puts a machine into while diagnosing a
/// routing problem and precisely when a leak would be captured into a ticket. Framework categories
/// keep the level the shipped <c>appsettings.json</c> gives them, because that file is what a
/// deployment gets and a harness that changed it would be testing itself.
/// </para>
/// <para>
/// <c>Warning</c> and above may name a network prefix, and deliberately do: an operator answering an
/// alert about a scan needs to know which prefix, and the criterion draws its line above that level
/// for exactly this reason.
/// </para>
/// </remarks>
public sealed class LogRedactionTests : IAsyncLifetime
{
    private const string Slug = "log-redaction";

    /// <summary>Values planted in the request that must not come out in a log line.</summary>
    private const string SecretCredential = "Bearer dle_pk_S3CR3T-CREDENTIAL-VALUE";

    private const string SecretQueryValue = "QUERY-SECRET-b6f21d";

    private const string SecretReferrerPath = "/private-page/REFERRER-SECRET-9ac";

    private const string ClientAddress = "198.18.40.77";

    private EdgeApiFactory _edge = null!;

    /// <inheritdoc />
    public ValueTask InitializeAsync()
    {
        _edge = new EdgeApiFactory();

        // Our own categories at Trace; Microsoft.* keeps whatever appsettings.json says, because the
        // more specific category wins and that file is the deployment's own decision.
        _edge.Overrides["Logging:LogLevel:Default"] = "Trace";
        _edge.Overrides["Dle:RateLimits:Edge:Enabled"] = "false";

        _edge.Links.Add(
            FakeDomainConfigStore.KnownHost,
            FakeLinkStore.WebLink(Slug, "https://www.example.com/promo"));

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _edge.Dispose();

        return ValueTask.CompletedTask;
    }

    [Fact]
    [Trait("Criterion", "S-08")]
    [Trait("Threat", "T-08")]
    public async Task Resolve_WithACredentialAQueryStringAReferrerAndAnAddress_LogsNoneOfThem()
    {
        _edge.Logs.Clear();

        using HttpClient client = _edge.CreateClientFrom(ClientAddress);

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri(
                "/" + Slug + "?utm_source=fb&session=" + SecretQueryValue + "&email=someone%40example.com",
                UriKind.Relative));

        request.Headers.TryAddWithoutValidation("Authorization", SecretCredential);
        request.Headers.TryAddWithoutValidation(
            "Referer",
            "https://mail.example.org" + SecretReferrerPath + "?token=REFERRER-TOKEN-11");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        AssertNotLogged("the Authorization header value", SecretCredential);
        AssertNotLogged("the credential without its scheme", "dle_pk_S3CR3T-CREDENTIAL-VALUE");
        AssertNotLogged("a query string value", SecretQueryValue);
        AssertNotLogged("an address in a query parameter", "someone@example.com");
        AssertNotLogged("the referrer's path", SecretReferrerPath);
        AssertNotLogged("a token carried in the referrer", "REFERRER-TOKEN-11");
        AssertNotLogged("the client's address", ClientAddress);
    }

    [Fact]
    [Trait("Criterion", "S-08")]
    [Trait("Threat", "T-08")]
    public async Task Resolve_OfAMissingSlug_LogsNeitherTheAddressNorTheQueryString()
    {
        // The 404 path logs a reason, which is the one place an operator gets to see *why* a request
        // was not served. That is exactly the line most likely to grow a "and here is everything about
        // the request" argument.
        _edge.Logs.Clear();

        using HttpClient client = _edge.CreateClientFrom("198.18.41.88");

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/no-such-thing?session=" + SecretQueryValue, UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        AssertNotLogged("a query string value", SecretQueryValue);
        AssertNotLogged("the client's address", "198.18.41.88");
    }

    [Fact]
    [Trait("Criterion", "S-08")]
    public async Task TheResolvePath_LogsItsOutcome_SoTheAssertionsAboveAreNotVacuous()
    {
        // Without this, an engine that logged nothing at all would pass every test in this class. The
        // resolve path is required to say what it served — "the redirect went somewhere unexpected" is
        // otherwise undiagnosable — and it says it at Debug, which is inside S-08's scope and is why
        // this host runs its own categories at Trace.
        _edge.Logs.Clear();

        using HttpClient client = _edge.CreateClientFrom("198.18.42.99");

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/" + Slug, UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        Assert.NotEmpty(_edge.Logs.AtInformationOrBelow);

        Assert.Contains(
            _edge.Logs.AtInformationOrBelow,
            entry => entry.Everything.Contains(Slug, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Criterion", "S-08")]
    public void EveryCapturedEntry_IsAttributedToACategory()
    {
        // A log line nobody can filter by category is a log line an operator cannot turn off, which is
        // how PII ends up shipped to a third-party sink by accident.
        Assert.All(
            _edge.Logs.Entries,
            entry => Assert.False(string.IsNullOrWhiteSpace(entry.Category)));
    }

    private void AssertNotLogged(string what, string value)
    {
        List<CapturedLog> offenders =
            [.. _edge.Logs.AtInformationOrBelow
                .Where(entry => entry.Everything.Contains(value, StringComparison.OrdinalIgnoreCase))];

        Assert.True(
            offenders.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 {what} reached the log at Information or below, which S-08 forbids and §E.2.2 T-08
                 calls a GDPR incident.

                 {string.Join(Environment.NewLine, offenders.Select(Describe))}
                 """));
    }

    private static string Describe(CapturedLog entry) => string.Create(
        CultureInfo.InvariantCulture,
        $"  [{entry.Level}] {entry.Category}: {entry.Everything}");
}
