using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Dle.SecurityTests.Enumeration;

/// <summary>
/// TC-102 and the edge half of TC-166: a missing slug and another tenant's slug are the same answer.
/// </summary>
/// <remarks>
/// <para>
/// TC-102 asks for "rovnaká odpoveď a rovnaký čas" — the same response and the same time — and T-07
/// explains what leaks otherwise: whether a campaign exists. A scanner that can tell "no such link"
/// from "not yours" has an oracle for the entire slug space of every other customer on the instance,
/// and it does not need the link to resolve for that to be worth money.
/// </para>
/// <para>
/// <b>Body first, timing second, and deliberately in that order.</b> The body is compared exactly,
/// which is a real and reproducible assertion. Timing is compared coarsely, as a sanity check with a
/// wide tolerance, because a microbenchmark inside a test runner on a shared machine measures the
/// garbage collector and the scheduler at least as much as it measures the code — and a timing test
/// tight enough to detect a real oracle would fail often enough to be muted within a month. A genuine
/// timing analysis belongs on a quiet machine with a statistical tool, and §D.7 puts it in the
/// penetration test.
/// </para>
/// </remarks>
[Collection(EdgeCollection.Name)]
public sealed class IndistinguishableResponseTests
{
    private const string OwnSlug = "tenant-a-link";

    private const string ForeignSlug = "tenant-b-link";

    /// <summary>The other tenant's host, which this deployment also serves.</summary>
    private const string ForeignHost = "link.other-tenant.test";

    private static readonly Guid TenantA = new("11111111-1111-1111-1111-111111111111");

    private static readonly Guid TenantB = new("33333333-3333-3333-3333-333333333333");

    /// <summary>Matches the per-response nonce, which is the one thing two identical pages may differ in.</summary>
    private static readonly Regex Nonce = new(
        "nonce-[A-Za-z0-9_-]{22}|nonce=\"[A-Za-z0-9_-]{22}\"",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly EdgeFixture _edge;

    public IndistinguishableResponseTests(EdgeFixture edge)
    {
        _edge = edge;

        _edge.Factory.Links.Add(
            FakeDomainConfigStore.KnownHost,
            FakeLinkStore.WebLink(OwnSlug, "https://www.example.com/a", TenantA));

        // The same engine serves this host for a different customer. From the first host's point of
        // view its slug must be as invisible as one that was never created.
        _edge.Factory.Links.Add(
            ForeignHost,
            FakeLinkStore.WebLink(ForeignSlug, "https://www.example.com/b", TenantB));
    }

    [Fact]
    [Trait("TestCase", "TC-102")]
    [Trait("Threat", "T-07")]
    public async Task Resolve_AMissingSlugAndAnotherTenantsSlug_ProduceByteIdenticalResponses()
    {
        using HttpClient client = _edge.Factory.CreateClientFrom("198.18.20.1");

        (HttpStatusCode missingStatus, string missingBody, string missingType, long? missingLength) =
            await ReadAsync(client, "/never-existed-at-all");

        (HttpStatusCode foreignStatus, string foreignBody, string foreignType, long? foreignLength) =
            await ReadAsync(client, "/" + ForeignSlug);

        Assert.Equal(HttpStatusCode.NotFound, missingStatus);
        Assert.Equal(missingStatus, foreignStatus);
        Assert.Equal(missingType, foreignType);
        Assert.Equal(missingLength, foreignLength);

        // Byte identical once the per-response nonce is masked. The nonce is fixed width and random by
        // construction, so it carries no information about which branch produced the page — and
        // masking it is what lets the rest of the document be compared exactly rather than loosely.
        Assert.Equal(Mask(missingBody), Mask(foreignBody));
    }

    [Fact]
    [Trait("TestCase", "TC-102")]
    [Trait("Threat", "T-07")]
    public async Task Resolve_EveryUnservableState_ProducesTheSameNotFoundDocument()
    {
        // Missing, inactive, not yet started, expired with no landing page, and a slug that cannot be
        // normalized at all. Five different reasons, one answer — and the reason is in the log, where
        // the operator can see it and the client cannot.
        _edge.Factory.Links.Add(
            FakeDomainConfigStore.KnownHost,
            FakeLinkStore.WebLink("inactive-one", "https://www.example.com/i") with { IsActive = false });

        _edge.Factory.Links.Add(
            FakeDomainConfigStore.KnownHost,
            FakeLinkStore.WebLink("future-one", "https://www.example.com/f") with
            {
                StartsAt = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });

        _edge.Factory.Links.Add(
            FakeDomainConfigStore.KnownHost,
            FakeLinkStore.WebLink("expired-one", "https://www.example.com/e") with
            {
                ExpiresAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });

        using HttpClient client = _edge.Factory.CreateClientFrom("198.18.21.1");

        string[] paths =
        [
            "/never-existed",
            "/" + ForeignSlug,
            "/inactive-one",
            "/future-one",
            "/expired-one",
            "/" + Uri.EscapeDataString("пример"),
        ];

        List<string> bodies = [];

        foreach (string path in paths)
        {
            (HttpStatusCode status, string body, _, _) = await ReadAsync(client, path);

            Assert.Equal(HttpStatusCode.NotFound, status);
            bodies.Add(Mask(body));
        }

        Assert.All(bodies, body => Assert.Equal(bodies[0], body));
    }

    [Fact]
    [Trait("TestCase", "TC-102")]
    [Trait("Threat", "T-07")]
    public async Task Resolve_AMissingSlugAndAnotherTenantsSlug_TakeComparableTime()
    {
        // A coarse sanity check, not a microbenchmark. The tolerance is wide on purpose: this is here
        // to catch a structural oracle — a branch that does a second lookup, renders a different page,
        // or hashes something extra — not to measure nanoseconds on a machine that is also running a
        // test host, a garbage collector and whatever else the developer has open.
        using HttpClient client = _edge.Factory.CreateClientFrom("198.18.22.1");

        // Warm both paths first. A cold JIT on the first branch measured would produce a difference
        // that has nothing to do with the code under test.
        for (int i = 0; i < 30; i++)
        {
            _ = await ReadAsync(client, "/warm-missing");
            _ = await ReadAsync(client, "/" + ForeignSlug);
        }

        TimeSpan missing = await MedianAsync(client, "/still-missing");
        TimeSpan foreign = await MedianAsync(client, "/" + ForeignSlug);

        double ratio = Math.Max(missing.TotalMilliseconds, foreign.TotalMilliseconds)
            / Math.Max(0.01, Math.Min(missing.TotalMilliseconds, foreign.TotalMilliseconds));

        Assert.True(
            ratio < 8.0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 The two 404 paths took materially different amounts of time, which is the shape of a
                 timing oracle: a client can ask whether a slug belongs to somebody else.
                   missing slug:   {missing.TotalMilliseconds:F3} ms (median of 40)
                   foreign slug:   {foreign.TotalMilliseconds:F3} ms (median of 40)
                   ratio:          {ratio:F2}
                 """));
    }

    private static async Task<TimeSpan> MedianAsync(HttpClient client, string path)
    {
        List<double> samples = [];

        for (int i = 0; i < 40; i++)
        {
            long started = Stopwatch.GetTimestamp();
            _ = await ReadAsync(client, path);
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        samples.Sort();

        return TimeSpan.FromMilliseconds(samples[samples.Count / 2]);
    }

    private static async Task<(HttpStatusCode Status, string Body, string ContentType, long? Length)> ReadAsync(
        HttpClient client,
        string path)
    {
        using HttpResponseMessage response = await client.GetAsync(
            new Uri(path, UriKind.Relative),
            TestContext.Current.CancellationToken);

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return (
            response.StatusCode,
            body,
            response.Content.Headers.ContentType?.ToString() ?? string.Empty,
            response.Content.Headers.ContentLength);
    }

    private static string Mask(string body) => Nonce.Replace(body, "nonce-XXXXXXXXXXXXXXXXXXXXXX");
}
