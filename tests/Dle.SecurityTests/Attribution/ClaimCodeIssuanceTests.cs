namespace Dle.SecurityTests.Attribution;

/// <summary>
/// The iOS interstitial shows a claim code only when the code can be redeemed.
/// </summary>
/// <remarks>
/// <para>
/// Redemption links an installation to a click, which the control plane allows only under the
/// <c>full</c> consent mode. A code shown under <c>aggregate_only</c> could never be redeemed — the
/// user would type it into the app and get nothing — and under <c>off</c> issuing it would store a
/// click identifier the mode promises is never stored. A code whose row could not be written is just
/// as unredeemable, so it is not shown either.
/// </para>
/// </remarks>
public sealed class ClaimCodeIssuanceTests : IDisposable
{
    private const string IosSafari =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) "
        + "Version/18.0 Mobile/15E148 Safari/604.1";

    private const string StoreUrl = "https://apps.apple.com/app/id123456789";

    private readonly EdgeApiFactory _factory = new();

    [Theory]
    [InlineData(ConsentMode.AggregateOnly)]
    [InlineData(ConsentMode.Off)]
    public async Task Interstitial_UnderAModeThatCannotRedeem_ShowsAndStoresNoCode(ConsentMode mode)
    {
        Seed("no-code", mode);

        string page = await FetchAsync("/no-code");

        Assert.DoesNotContain("one-time code", page, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_factory.ClaimCodes.Records);
    }

    [Fact]
    public async Task Interstitial_UnderFullMode_ShowsTheCodeItStored()
    {
        Seed("with-code", ConsentMode.Full);

        string page = await FetchAsync("/with-code");

        Dle.Domain.Entities.ClaimCodeRecord record = Assert.Single(_factory.ClaimCodes.Records);
        Assert.Contains("one-time code", page, StringComparison.OrdinalIgnoreCase);
        Assert.True(record.ExpiresAt > record.CreatedAt);
    }

    [Fact]
    public async Task Interstitial_WhenTheCodeCannotBeStored_ShowsNoCode()
    {
        Seed("store-down", ConsentMode.Full);
        _factory.ClaimCodes.Fail = true;

        string page = await FetchAsync("/store-down");

        Assert.DoesNotContain("one-time code", page, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public void Dispose() => _factory.Dispose();

    private void Seed(string slug, ConsentMode mode) =>
        _factory.Links.Add(
            FakeDomainConfigStore.KnownHost,
            FakeLinkStore.InterstitialLink(slug, "https://www.example.com/promo", StoreUrl) with
            {
                TenantConsentMode = mode,
            });

    private async Task<string> FetchAsync(string path)
    {
        using HttpClient client = _factory.CreateClientFrom("198.51.100.17");
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("User-Agent", IosSafari);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
