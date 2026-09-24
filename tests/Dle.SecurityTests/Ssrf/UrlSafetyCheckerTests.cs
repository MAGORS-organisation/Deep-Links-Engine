using Dle.Control.Features.Abuse;
using Dle.Control.Features.Webhooks;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dle.SecurityTests.Ssrf;

/// <summary>
/// The composed SSRF gate: syntax, then the resolved address, then reputation (T-02, TC-162, TC-163).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TargetUrlPolicyTests"/> covers what can be decided from the string.
/// <see cref="DefaultUrlSafetyChecker"/> is the piece that adds the step §E.3 actually calls the SSRF
/// defence — "rezolvovať a kontrolovať výslednú IP, nie len hostname" — and
/// <see cref="WebhookDestinationPolicy"/> is the same judgement applied to the other customer-supplied
/// URL the deployment fetches on a schedule.
/// </para>
/// <para>
/// <b>The DNS-rebinding case, honestly.</b> A true rebinding test needs a name that resolves to a
/// forbidden address, and both of these components call <see cref="System.Net.Dns"/> statically. There
/// is no resolver seam to substitute, so the test below drives the branch that <em>can</em> be reached
/// without a network — a name that does not resolve at all, which must fail closed — and the
/// resolved-address branch is exercised through address literals, which take the same code path with
/// the resolution already done. The gap is named in the test's own message rather than papered over,
/// and closing it is a one-line change in the product: take an <c>IHostResolver</c> instead of calling
/// the static class.
/// </para>
/// </remarks>
public sealed class UrlSafetyCheckerTests : IDisposable
{
    private static readonly FixedTimeProvider Clock = new();

    /// <summary>
    /// One meter for the whole class. It is an instrument registry, not per-call state, and the
    /// analyzer is right that something has to own it.
    /// </summary>
    private readonly AbuseMetrics _metrics = new(meterFactory: null);

    /// <inheritdoc />
    public void Dispose() => _metrics.Dispose();

    [Theory]
    [Trait("TestCase", "TC-162")]
    [Trait("Threat", "T-02")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::ffff:169.254.169.254]/latest/meta-data/")]
    [InlineData("http://10.0.0.1/admin")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://[::1]:6379/")]
    [InlineData("http://100.100.100.200/latest/meta-data/")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://kubernetes.default.svc.cluster.local/api")]
    public async Task CheckAsync_ATargetInsideTheDeploymentsOwnNetwork_IsRefused(string url)
    {
        UrlSafetyVerdict verdict = await CreateChecker().CheckAsync(url, TestContext.Current.CancellationToken);

        Assert.NotEqual(UrlSafetyLevel.Safe, verdict.Level);
    }

    [Fact]
    [Trait("TestCase", "TC-162")]
    [Trait("Threat", "T-02")]
    public async Task CheckAsync_AHostThatDoesNotResolve_FailsClosed()
    {
        // The rebinding-shaped case that is reachable without a network. RFC 2606 guarantees `.invalid`
        // never resolves, so this drives the resolution branch and asserts the property that matters
        // there: the answer to "we could not establish where this points" is refusal, never approval.
        //
        // The complementary case — a name that resolves to a forbidden address — cannot be written
        // here, because DefaultUrlSafetyChecker calls System.Net.Dns statically and there is no seam
        // to substitute a resolver at. See the class remarks: this is a product change, not a test one.
        UrlSafetyVerdict verdict = await CreateChecker()
            .CheckAsync("https://this-name-will-never-resolve.invalid/", TestContext.Current.CancellationToken);

        Assert.NotEqual(UrlSafetyLevel.Safe, verdict.Level);
        Assert.Equal("private_ip", verdict.Source);
    }

    [Theory]
    [Trait("TestCase", "TC-161")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("intent://scan/#Intent;scheme=zxing;end")]
    public async Task CheckAsync_ANonWebScheme_IsRefusedBeforeAnyLookup(string url)
    {
        UrlSafetyVerdict verdict = await CreateChecker().CheckAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(UrlSafetyLevel.Blocked, verdict.Level);
        Assert.Equal("syntax", verdict.Source);
    }

    [Fact]
    [Trait("TestCase", "TC-163")]
    [Trait("Threat", "T-01")]
    public async Task Blocklist_AHostTheOperatorListed_IsRefusedAndAttributedToTheBlocklist()
    {
        // Asserted against the reputation provider directly rather than through the composed checker,
        // and that is a statement about the product's own ordering rather than a convenience. §E.3
        // runs syntax, then the resolved address, then reputation, and it stops at the first refusal —
        // so a blocklisted host that does not resolve is refused by the address step and never reaches
        // the blocklist at all. Driving the provider is the only way to assert what TC-163 actually
        // asks for: that the rejection is attributed, so an operator answering an appeal can say which
        // source objected.
        string blocklist = await WriteBlocklistAsync(["phish.example", "malware.example"]);

        try
        {
            BlocklistReputationProvider provider = CreateBlocklist(blocklist);

            Assert.True(provider.IsEnabled);

            UrlSafetyVerdict? verdict = await provider.CheckAsync(
                new Uri("https://phish.example/login"),
                TestContext.Current.CancellationToken);

            Assert.NotNull(verdict);
            Assert.Equal(UrlSafetyLevel.Blocked, verdict.Level);
            Assert.Equal(BlocklistReputationProvider.SourceName, verdict.Source);
            Assert.Contains("blocklist", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(verdict.CheckedAt);
        }
        finally
        {
            File.Delete(blocklist);
        }
    }

    [Fact]
    [Trait("TestCase", "TC-163")]
    public async Task Blocklist_ASubdomainOfABlockedHost_IsRefusedToo()
    {
        // An abuser who owns one host owns all of them, so a blocklist that only matches the exact
        // label is a blocklist that is bypassed with one DNS record.
        string blocklist = await WriteBlocklistAsync(["phish.example"]);

        try
        {
            UrlSafetyVerdict? verdict = await CreateBlocklist(blocklist).CheckAsync(
                new Uri("https://login.secure.phish.example/"),
                TestContext.Current.CancellationToken);

            Assert.NotNull(verdict);
            Assert.Equal(BlocklistReputationProvider.SourceName, verdict.Source);
        }
        finally
        {
            File.Delete(blocklist);
        }
    }

    [Fact]
    [Trait("TestCase", "TC-163")]
    public async Task Blocklist_AHostTheOperatorDidNotList_IsNotApproved()
    {
        // Absence from a local blocklist is not evidence of safety, so the source stays silent rather
        // than voting "safe" — otherwise one enabled provider would short-circuit every other check.
        string blocklist = await WriteBlocklistAsync(["phish.example"]);

        try
        {
            UrlSafetyVerdict? verdict = await CreateBlocklist(blocklist).CheckAsync(
                new Uri("https://www.example.com/"),
                TestContext.Current.CancellationToken);

            Assert.Null(verdict);
        }
        finally
        {
            File.Delete(blocklist);
        }
    }

    [Fact]
    [Trait("TestCase", "TC-163")]
    public async Task CheckAsync_ABlocklistedHost_IsNeverApproved()
    {
        // The composed judgement. Which step refuses depends on whether the name resolves on the
        // machine running the tests, and that is exactly why this asserts the outcome and not the
        // source: an offline runner refuses at the address step, an online one at the blocklist, and
        // approving the target is wrong in both.
        string blocklist = await WriteBlocklistAsync(["phish.example"]);

        try
        {
            UrlSafetyVerdict verdict = await CreateChecker(blocklist).CheckAsync(
                "https://phish.example/login",
                TestContext.Current.CancellationToken);

            Assert.NotEqual(UrlSafetyLevel.Safe, verdict.Level);
        }
        finally
        {
            File.Delete(blocklist);
        }
    }

    [Theory]
    [Trait("TestCase", "TC-162")]
    [Trait("Threat", "T-02")]
    [InlineData("https://169.254.169.254/hook")]
    [InlineData("https://10.1.2.3/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[::ffff:10.0.0.1]/hook")]
    [InlineData("https://100.64.1.1/hook")]
    [InlineData("https://metadata.google.internal/hook")]
    [InlineData("https://localhost/hook")]
    public async Task WebhookDestination_PointingIntoTheDeploymentsNetwork_IsRefused(string url)
    {
        // A webhook destination is fetched from inside the deployment on a schedule, which makes it the
        // same SSRF primitive as a link target with a timer attached.
        WebhookDestinationVerdict verdict = await WebhookDestinationPolicy.ValidateAsync(
            url,
            allowPrivate: false,
            TestContext.Current.CancellationToken);

        Assert.False(verdict.Accepted, url + " must not be registerable as a webhook destination.");
    }

    [Theory]
    [Trait("Threat", "T-02")]
    [InlineData("http://127.0.0.1:8080/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("http://192.168.1.10:9000/hook")]
    public async Task WebhookDestination_InsideTheNetwork_OpensOnlyForAnOperatorWhoAskedForIt(string url)
    {
        // Dle:Webhooks:AllowPrivateDestinations is the one thing that opens this, and it has to
        // actually open it: the address rules used to run before the switch was read, so an
        // operator who set it still could not point a subscription at a listener on their own
        // machine, and a test could not watch a delivery arrive. Both directions are pinned here,
        // because a switch that silently does nothing and a switch that is on by mistake fail in
        // opposite directions and only one of them is visible in a log.
        WebhookDestinationVerdict refused = await WebhookDestinationPolicy.ValidateAsync(
            url,
            allowPrivate: false,
            TestContext.Current.CancellationToken);

        WebhookDestinationVerdict allowed = await WebhookDestinationPolicy.ValidateAsync(
            url,
            allowPrivate: true,
            TestContext.Current.CancellationToken);

        Assert.False(refused.Accepted, url + " must be refused on an instance that has not asked for it.");
        Assert.True(allowed.Accepted, url + " must be reachable once the operator has asked for it: " + allowed.Reason);
    }

    [Fact]
    [Trait("Threat", "T-02")]
    public async Task WebhookDestination_OverPlainHttp_IsRefused()
    {
        // A signature proves origin, not confidentiality, and the payload carries attribution data
        // about the customer's own users.
        WebhookDestinationVerdict verdict = await WebhookDestinationPolicy.ValidateAsync(
            "http://hooks.example.com/dle",
            allowPrivate: false,
            TestContext.Current.CancellationToken);

        Assert.False(verdict.Accepted);
        Assert.Contains("https", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Threat", "T-02")]
    public async Task WebhookDestination_ThatDoesNotResolve_FailsClosed()
    {
        WebhookDestinationVerdict verdict = await WebhookDestinationPolicy.ValidateAsync(
            "https://this-name-will-never-resolve.invalid/hook",
            allowPrivate: false,
            TestContext.Current.CancellationToken);

        Assert.False(verdict.Accepted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//hooks.example.com/dle")]
    public async Task WebhookDestination_ThatIsNotAnAbsoluteWebUrl_IsRefused(string? url)
    {
        WebhookDestinationVerdict verdict = await WebhookDestinationPolicy.ValidateAsync(
            url,
            allowPrivate: false,
            TestContext.Current.CancellationToken);

        Assert.False(verdict.Accepted);
    }

    [Fact]
    public void WebhookEventTypes_AnUnknownTypeIsRefusedAtSubscriptionTime()
    {
        // Not SSRF, but the same fail-closed posture on the same endpoint: a subscription to a type
        // that does not exist is a customer waiting forever for a callback that cannot come.
        Assert.False(
            WebhookDestinationPolicy.TryNormalizeEventTypes(["attribution.create"], out _, out string? reason));

        Assert.NotNull(reason);
    }

    private static BlocklistReputationProvider CreateBlocklist(string path) =>
        new(
            new StaticOptionsMonitor<AbuseOptions>(new AbuseOptions
            {
                UrlHausEnabled = false,
                BlocklistPath = path,
            }),
            Clock,
            NullLogger<BlocklistReputationProvider>.Instance);

    private DefaultUrlSafetyChecker CreateChecker(string? blocklistPath = null)
    {
        AbuseOptions options = new()
        {
            UrlHausEnabled = false,
            BlocklistPath = blocklistPath,
        };

        List<IUrlReputationProvider> providers = [];

        if (blocklistPath is not null)
        {
            providers.Add(CreateBlocklist(blocklistPath));
        }

        return new DefaultUrlSafetyChecker(
            providers,
            _metrics,
            Clock,
            NullLogger<DefaultUrlSafetyChecker>.Instance);
    }

    private static async Task<string> WriteBlocklistAsync(IEnumerable<string> hosts)
    {
        string path = Path.Combine(Path.GetTempPath(), "dle-blocklist-" + Guid.NewGuid().ToString("N") + ".txt");

        await File.WriteAllLinesAsync(path, hosts, TestContext.Current.CancellationToken);

        return path;
    }

    /// <summary>An options monitor over one immutable value.</summary>
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
