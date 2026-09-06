namespace Dle.SecurityTests.Enumeration;

/// <summary>
/// TC-108 / T-07: the anti-enumeration budget of §E.9, and its independence from the success budget.
/// </summary>
/// <remarks>
/// <para>
/// §E.9 calls the limit on 404 responses the most important one in the table, and says why: it is the
/// primary defence against slug enumeration and "musí byť oddelený od limitu na úspešné požiadavky,
/// inak by legitímna špička kampane vypla ochranu". Two counters, two budgets, exhaustible
/// independently — that is the property, and it is the one a single shared counter would quietly
/// break while every other test still passed.
/// </para>
/// <para>
/// This class builds its own host rather than sharing the suite's, because a rate limiter is process
/// state: a budget drained here would silently change what every other test in the assembly is
/// looking at. Each test also uses its own network prefix, because the budget is keyed by prefix and
/// the shadow ban outlives the request that armed it.
/// </para>
/// </remarks>
public sealed class NotFoundBudgetTests : IAsyncLifetime
{
    /// <summary>The §E.9 defaults this host runs with: 20 tokens a minute, burst 40, ban 15 minutes.</summary>
    private const int Burst = 40;

    private const string Slug = "ab3xk9pq";

    private const string TargetUrl = "https://www.example.com/promo/autumn";

    private EdgeApiFactory _edge = null!;

    /// <inheritdoc />
    public ValueTask InitializeAsync()
    {
        _edge = new EdgeApiFactory();

        // Only the resolve sliding window is lifted, and only so that a test making a few hundred
        // requests from one address does not trip the *other* limit and mask the one under test. The
        // 404 budget keeps its §E.9 defaults, which is the whole subject here.
        _edge.Overrides["Dle:RateLimits:Edge:Resolve:PermitsPerWindow"] = "100000";

        _edge.Links.Add(FakeDomainConfigStore.KnownHost, FakeLinkStore.WebLink(Slug, TargetUrl));

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _edge.Dispose();

        return ValueTask.CompletedTask;
    }

    [Fact]
    [Trait("TestCase", "TC-108")]
    [Trait("Threat", "T-07")]
    public async Task Resolve_ManyDistinctMissingSlugsFromOnePrefix_ExhaustsThe404BudgetAndShadowBansThePrefix()
    {
        using HttpClient scanner = _edge.CreateClientFrom("203.0.113.7");

        List<HttpStatusCode> answers = [];

        for (int i = 0; i < Burst + 5; i++)
        {
            answers.Add(await StatusOfAsync(scanner, "/missing" + i.ToString(CultureInfo.InvariantCulture)));
        }

        // The bucket holds `Burst` tokens, so the first `Burst` misses are ordinary 404s and the one
        // that drains it is answered 429 — once. After that the prefix is shadow banned and every
        // request looks exactly like an ordinary miss again, which is what §E.9 asks for: the scanner
        // must not be handed a reliable "you have been detected" oracle.
        Assert.All(answers.Take(Burst), status => Assert.Equal(HttpStatusCode.NotFound, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, answers[Burst]);

        Assert.All(
            answers.Skip(Burst + 1),
            status => Assert.Equal(HttpStatusCode.NotFound, status));
    }

    [Fact]
    [Trait("TestCase", "TC-108")]
    [Trait("Threat", "T-07")]
    public async Task Resolve_AShadowBannedPrefix_IsAnsweredWithoutTouchingTheStore()
    {
        using HttpClient scanner = _edge.CreateClientFrom("192.0.2.7");

        for (int i = 0; i <= Burst; i++)
        {
            _ = await StatusOfAsync(scanner, "/absent" + i.ToString(CultureInfo.InvariantCulture));
        }

        long before = _edge.Links.Lookups;

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(scanner, "/" + Slug));
        }

        // Ten requests for a slug that certainly exists, and the store was never asked. That is the
        // point of the ban: a scan that has proved what it is costs the service nothing, and the
        // defence cannot itself be turned into the denial of service it was installed to prevent.
        Assert.Equal(before, _edge.Links.Lookups);
    }

    [Fact]
    [Trait("TestCase", "TC-108")]
    [Trait("Threat", "T-07")]
    public async Task Resolve_LegitimateTrafficFromAnotherPrefix_IsUnaffectedByAScanElsewhere()
    {
        using HttpClient scanner = _edge.CreateClientFrom("198.51.100.7");

        for (int i = 0; i <= Burst + 20; i++)
        {
            _ = await StatusOfAsync(scanner, "/nothere" + i.ToString(CultureInfo.InvariantCulture));
        }

        using HttpClient ordinary = _edge.CreateClientFrom("198.18.7.7");

        for (int i = 0; i < 25; i++)
        {
            Assert.Equal(HttpStatusCode.Found, await StatusOfAsync(ordinary, "/" + Slug));
        }
    }

    [Fact]
    [Trait("TestCase", "TC-108")]
    [Trait("Threat", "T-07")]
    public async Task Resolve_SuccessfulTraffic_DoesNotConsumeThe404Budget()
    {
        // The independence claim, from the side that a shared counter would break first. This client
        // resolves a real link two hundred times — a modest campaign spike — and only then starts
        // missing. If the two budgets were one counter, the ban would already have been armed and the
        // very first miss would be answered 429; because they are separate, the full burst is still
        // there.
        using HttpClient client = _edge.CreateClientFrom("198.18.8.8");

        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(HttpStatusCode.Found, await StatusOfAsync(client, "/" + Slug));
        }

        for (int i = 0; i < Burst; i++)
        {
            Assert.Equal(
                HttpStatusCode.NotFound,
                await StatusOfAsync(client, "/after" + i.ToString(CultureInfo.InvariantCulture)));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusOfAsync(client, "/after-last"));
    }

    [Fact]
    [Trait("TestCase", "TC-108")]
    [Trait("Threat", "T-07")]
    public async Task Resolve_ExhaustingThe404Budget_DoesNotConsumeAnotherPrefixesBudget()
    {
        // The same independence, across prefixes rather than across budgets: the guard's key space is
        // chosen by the attacker, and a scan from one allocation must not spend anybody else's tokens.
        using HttpClient first = _edge.CreateClientFrom("198.18.9.9");

        for (int i = 0; i <= Burst; i++)
        {
            _ = await StatusOfAsync(first, "/x" + i.ToString(CultureInfo.InvariantCulture));
        }

        using HttpClient second = _edge.CreateClientFrom("198.18.10.10");

        for (int i = 0; i < Burst; i++)
        {
            Assert.Equal(
                HttpStatusCode.NotFound,
                await StatusOfAsync(second, "/y" + i.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static async Task<HttpStatusCode> StatusOfAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(
            new Uri(path, UriKind.Relative),
            TestContext.Current.CancellationToken);

        return response.StatusCode;
    }
}
