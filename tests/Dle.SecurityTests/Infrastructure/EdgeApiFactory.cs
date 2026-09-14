using Dle.Edge.Configuration;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dle.SecurityTests.Infrastructure;

/// <summary>
/// Runs the real edge pipeline in-process with the two ports that need a server replaced.
/// </summary>
/// <remarks>
/// <para>
/// Everything the security assertions are about is left alone: the routing engine, the URL builder,
/// the scheme allow list, the consent gate, the rate limiters, the enumeration guard, the security
/// header middleware and the page renderers are the shipped ones, wired by the shipped composition
/// root. Only the link store, the domain configuration store, the click sink and the crawler verifier
/// are substituted, because those are the four seams that would otherwise need PostgreSQL and a DNS
/// round trip.
/// </para>
/// <para>
/// The substitutions are registered after the modules have run. Every registration in the edge's
/// modules uses <c>TryAdd</c>, so an override added here is simply the last one and wins — which is the
/// seam the modules document.
/// </para>
/// </remarks>
public sealed class EdgeApiFactory : WebApplicationFactory<EdgeOptions>
{
    /// <summary>Header the harness reads the pretend client address from.</summary>
    /// <remarks>
    /// The rate limit partition, the hashed identifier in the click stream and the geographic lookup
    /// all derive from <c>RemoteIpAddress</c>, which a test server leaves null. Without a way to set it
    /// every request in a run shares one bucket, and the one property TC-108 is about — that a
    /// scanner's prefix is banned and everybody else's is not — could not be expressed at all.
    /// </remarks>
    public const string ClientAddressHeader = "X-Dle-Test-Client-Ip";

    /// <summary>Guards the process-wide environment while a host is being built.</summary>
    private static readonly object EnvironmentLock = new();

    /// <summary>The in-memory link store the tests seed.</summary>
    public FakeLinkStore Links { get; } = new();

    /// <summary>The click stream the resolve pipeline wrote to.</summary>
    public RecordingClickSink Clicks { get; } = new();

    /// <summary>Everything the host logged.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Whether a user agent claiming to be a crawler is confirmed.</summary>
    /// <remarks>
    /// Off by default, which is the TC-107 posture: an unconfirmed claim is served as an ordinary
    /// browser. The Open Graph paths turn it on.
    /// </remarks>
    public bool ConfirmCrawlers { get; init; }

    /// <summary>Configuration the edge needs in order to build with nothing behind it.</summary>
    public static IReadOnlyDictionary<string, string?> Settings { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Parsed at registration; never opened, because the link store is substituted.
            ["ConnectionStrings:Postgres"] =
                "Host=127.0.0.1;Port=1;Database=dle_security_tests;Username=dle;Password=dle;Timeout=1;Command Timeout=1",

            ["Dle:Crypto:MasterSecret"] = "dle-security-tests-master-secret-000000000000000",

            // No database file on this machine, and NFR-14 already requires the edge to treat a missing
            // one as "no geography" rather than as an error.
            ["Dle:Edge:GeoIp:Provider"] = "None",
            ["Dle:Edge:GeoIp:AutoUpdate"] = "false",

            // Logging is deliberately NOT overridden here. src/Dle.Edge/appsettings.json is loaded
            // from the content root like it is in a deployment, and S-08 is a claim about what a
            // deployment emits — a harness that quietened the framework's own request logging would
            // turn the log redaction tests into a check of the harness.
        };

    /// <summary>
    /// Configuration applied after <see cref="Settings"/>, so one test class can change one limit.
    /// </summary>
    /// <remarks>
    /// Rate limiter and enumeration-guard state is process-wide and outlives a request, so a suite that
    /// shared one host between the tests that exhaust a budget and the tests that merely make a few
    /// thousand requests would have the first silently change the meaning of the second. The tests that
    /// are about a limit build their own host with the §E.9 defaults; everybody else raises the limits
    /// out of the way here.
    /// </remarks>
    public Dictionary<string, string?> Overrides { get; } = new(StringComparer.Ordinal);

    /// <summary>Creates a client that presents a chosen address and host.</summary>
    /// <param name="clientAddress">Address the pipeline should see as the peer.</param>
    /// <param name="host">Host header, defaulting to the configured link domain.</param>
    /// <returns>The client.</returns>
    public HttpClient CreateClientFrom(string clientAddress, string host = FakeDomainConfigStore.KnownHost)
    {
        HttpClient client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://" + host + "/"),
        });

        client.DefaultRequestHeaders.Add(ClientAddressHeader, clientAddress);

        return client;
    }

    /// <summary>
    /// Builds the host with the test configuration visible to the composition root itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The edge is a slim minimal-API host: <c>Program.cs</c> reads <c>builder.Configuration</c> while
    /// it is registering modules, and <c>AddDleFastPersistence</c> throws outright when the connection
    /// string is absent. A test factory's <c>ConfigureAppConfiguration</c> delta only lands when the
    /// host is built, which is after those registrations have already run and thrown — so it cannot
    /// supply configuration the composition root reads eagerly.
    /// </para>
    /// <para>
    /// Environment variables are read by the builder's own provider at the moment the builder is
    /// constructed, so setting them around the build is the one channel that arrives in time. They are
    /// process-wide, which is why the window is held under a lock and closed again in a
    /// <c>finally</c>: two factories building at once would otherwise see each other's settings.
    /// </para>
    /// </remarks>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        Dictionary<string, string?> applied = new(Settings, StringComparer.Ordinal);

        foreach ((string key, string? value) in Overrides)
        {
            applied[key] = value;
        }

        lock (EnvironmentLock)
        {
            try
            {
                foreach ((string key, string? value) in applied)
                {
                    Environment.SetEnvironmentVariable(EnvironmentName(key), value);
                }

                return base.CreateHost(builder);
            }
            finally
            {
                foreach (string key in applied.Keys)
                {
                    Environment.SetEnvironmentVariable(EnvironmentName(key), null);
                }
            }
        }
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Production);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(Settings);
            configuration.AddInMemoryCollection(Overrides);
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILinkStore>(Links);
            services.AddSingleton<IClickEventSink>(Clicks);
            services.AddSingleton<IDomainConfigStore, FakeDomainConfigStore>();

            if (ConfirmCrawlers)
            {
                services.AddSingleton<IBotVerifier, ConfirmingBotVerifier>();
            }
            else
            {
                services.AddSingleton<IBotVerifier, DenyingBotVerifier>();
            }

            services.AddSingleton<ILoggerProvider>(Logs);

            // Ahead of everything, including the security headers and the rate limiter, because those
            // are the components whose behaviour depends on which client is asking.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IStartupFilter, ClientAddressStartupFilter>());
        });
    }

    /// <summary>Renders a configuration key the way the environment variable provider spells it.</summary>
    /// <param name="key">The configuration key, colon separated.</param>
    /// <returns>The environment variable name.</returns>
    private static string EnvironmentName(string key) => key.Replace(":", "__", StringComparison.Ordinal);

    /// <summary>
    /// Inserts the address-spoofing middleware at the head of the pipeline.
    /// </summary>
    /// <remarks>
    /// A start-up filter rather than a call inside the application, because the header has to be
    /// applied before the first component that reads the address — and that component is in the
    /// product, not here.
    /// </remarks>
    private sealed class ClientAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            ArgumentNullException.ThrowIfNull(next);

            return app =>
            {
                app.Use(static async (context, following) =>
                {
                    if (context.Request.Headers.TryGetValue(ClientAddressHeader, out var value)
                        && IPAddress.TryParse(value.ToString(), out IPAddress? address))
                    {
                        context.Connection.RemoteIpAddress = address;
                    }

                    await following(context);
                });

                next(app);
            };
        }
    }
}
