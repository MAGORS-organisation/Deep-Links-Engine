using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

using Dle.Control.Configuration;
using Dle.Control.Identity;
using Dle.Domain.Contracts;
using Dle.Persistence.Tenancy;
using Dle.SecurityTests.Infrastructure;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.SecurityTests.Tenancy;

/// <summary>
/// T-09: a request is scoped to exactly the tenant it is authorised as.
/// </summary>
/// <remarks>
/// <para>
/// The defect this pins down: the default authentication scheme picked the SDK key when an
/// <c>X-Dle-Sdk-Key</c> header was present, the tenant scope was opened from that principal, and then
/// the endpoint's policy re-authenticated with the control-plane key scheme and authorised a different
/// tenant's key. An SDK key is extracted from any APK, so one's own API key plus a victim's SDK key was
/// a cross-tenant write.
/// </para>
/// <para>
/// Three independent barriers now stand in the way and each is asserted on the shipped control-plane
/// pipeline: a request with more than one credential is refused before any scheme runs; the tenant
/// scope is opened after authorization from the principal authorization accepted; and a principal that
/// names two tenants has no tenant (unit-tested with the claim mapper).
/// </para>
/// </remarks>
public sealed class MixedCredentialTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly ControlHost _host = new();

    public static TheoryData<string, string, string, string> CredentialPairs() => new()
    {
        { "X-Dle-Sdk-Key", "dlk_aaaaaaaa_sdk", "X-Api-Key", "dle_bbbbbbbb_api" },
        { "X-Dle-Sdk-Key", "dlk_aaaaaaaa_sdk", "Authorization", "Bearer dle_bbbbbbbb_api" },
        { "Authorization", "Bearer dlk_aaaaaaaa_sdk", "X-Api-Key", "dle_bbbbbbbb_api" },
    };

    [Theory]
    [MemberData(nameof(CredentialPairs))]
    public async Task TwoCredentials_AreRefusedBeforeAnySchemeRuns(
        string firstHeader,
        string firstValue,
        string secondHeader,
        string secondValue)
    {
        using HttpClient client = _host.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/links");
        request.Headers.TryAddWithoutValidation(firstHeader, firstValue);
        request.Headers.TryAddWithoutValidation(secondHeader, secondValue);

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        JsonObject? problem = JsonNode.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)) as JsonObject;

        Assert.Equal(ProblemCodes.ValidationFailed, problem?["type"]?.GetValue<string>());
        Assert.Empty(_host.Scopes);
    }

    [Fact]
    public async Task TenantScope_ComesFromThePrincipalAuthorizationAccepted()
    {
        // The default scheme is rigged to answer tenant A for every request, while the endpoint's
        // policy authenticates the control-plane key scheme, which answers tenant B. Whatever the
        // endpoint then reads must be scoped to B — the tenant it was authorised as.
        using HttpClient client = _host.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/links");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer dle_bbbbbbbb_api");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(TenantB, _host.Scopes);
        Assert.DoesNotContain(TenantA, _host.Scopes);
    }

    /// <inheritdoc />
    public void Dispose() => _host.Dispose();

    /// <summary>The shipped control plane with two authentication seams rigged and the tenant scope observed.</summary>
    private sealed class ControlHost : WebApplicationFactory<DleControlOptions>
    {
        private static readonly IReadOnlyDictionary<string, string?> Settings =
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:Postgres"] =
                    "Host=127.0.0.1;Port=1;Database=dle_security_tests;Username=dle;Password=dle;Timeout=1;Command Timeout=1",
                ["ConnectionStrings:Valkey"] = string.Empty,
                ["Dle:Crypto:MasterSecret"] = "dle-security-tests-master-secret-000000000000000",
                ["Dle:Workers:Enabled"] = "false",
                ["Dle:Bootstrap:Enabled"] = "false",
                ["Dle:Control:ServeAdminSpa"] = "false",
                ["Dle:Telemetry:Otlp:Enabled"] = "false",
            };

        private readonly RecordingTenantContext _tenants = new();

        public IReadOnlyCollection<Guid> Scopes => _tenants.Scopes;

        protected override IHost CreateHost(IHostBuilder builder) =>
            TestHostEnvironment.RunWithEnvironment(Settings, () => base.CreateHost(builder));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ITenantContext>(_tenants);

                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, FixedTenantHandler>("probe", _ => { });

                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = "probe";
                    options.SchemeMap[DleAuthenticationSchemes.ApiKey].HandlerType = typeof(TenantBKeyHandler);
                });
            });
        }
    }

    /// <summary>Stands in for whatever the default scheme picked: always tenant A, as an owner.</summary>
    private sealed class FixedTenantHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(Ticket(TenantA, Scheme.Name)));
    }

    /// <summary>Stands in for the control-plane key scheme: tenant B, as an owner.</summary>
    private sealed class TenantBKeyHandler(
        IOptionsMonitor<DleKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<DleKeyAuthenticationOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Request.Headers.Authorization.Count > 0
                ? AuthenticateResult.Success(Ticket(TenantB, Scheme.Name))
                : AuthenticateResult.NoResult());
    }

    private static AuthenticationTicket Ticket(Guid tenant, string scheme) =>
        new(
            new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(DleClaimTypes.TenantId, tenant.ToString()),
                    new Claim(DleClaimTypes.Role, DleRoles.Owner),
                    new Claim(DleClaimTypes.ActorType, DleCaller.ApiKeyActor),
                ],
                scheme)),
            scheme);

    /// <summary>The shipped ambient tenant context, recording every scope it opens.</summary>
    private sealed class RecordingTenantContext : ITenantContext
    {
        private readonly AmbientTenantContext _inner = new();
        private readonly ConcurrentQueue<Guid> _scopes = new();

        public IReadOnlyCollection<Guid> Scopes => _scopes;

        public Guid? TenantId => _inner.TenantId;

        public Guid RequiredTenantId => _inner.RequiredTenantId;

        public IDisposable BeginScope(Guid tenantId)
        {
            _scopes.Enqueue(tenantId);
            return _inner.BeginScope(tenantId);
        }
    }
}
