using Dle.Control.Configuration;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dle.ContractTests.Infrastructure;

/// <summary>
/// Boots the control plane in-process purely so that its published surface can be inspected.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here talks to PostgreSQL or to Valkey, and nothing in this assembly makes a request that
/// would. The OpenAPI document, the problem type table and the wire shapes are all decided at
/// composition time, so the host has to be <em>built</em> but never has to be <em>reachable</em>.
/// The connection string below is syntactically valid and deliberately unroutable: it satisfies
/// <c>AddDlePersistence</c>, which refuses to register without one, and any test that accidentally
/// issued a query against it would fail loudly rather than silently pass.
/// </para>
/// <para>
/// The background workers are switched off. They are leader-elected against a PostgreSQL advisory
/// lock, and a contract test run should not spend its first ten seconds watching six workers fail to
/// take a lock on a database that is not there.
/// </para>
/// <para>
/// The entry point type parameter is <see cref="DleControlOptions"/> rather than <c>Program</c>.
/// Both <c>Dle.Edge</c> and <c>Dle.Control</c> declare a global <c>Program</c> class for exactly this
/// purpose and this assembly references both, so the name is ambiguous here;
/// <see cref="WebApplicationFactory{TEntryPoint}"/> only ever uses the type to find its assembly.
/// </para>
/// </remarks>
public sealed class ControlApiFactory : WebApplicationFactory<DleControlOptions>
{
    /// <summary>Configuration the host needs in order to build, with no live dependency behind it.</summary>
    public static IReadOnlyDictionary<string, string?> Settings { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Parsed by Npgsql at registration, never opened by a contract test.
            ["ConnectionStrings:Postgres"] =
                "Host=127.0.0.1;Port=1;Database=dle_contract_tests;Username=dle;Password=dle;Timeout=1;Command Timeout=1",

            // Deterministic, and only ever used to derive keys whose public halves appear in the
            // published document. Not a secret: this host answers no authenticated request.
            ["Dle:Crypto:MasterSecret"] = "dle-contract-tests-master-secret-0000000000000000",

            ["Dle:Workers:Enabled"] = "false",
            ["Dle:Control:ServeAdminSpa"] = "false",
            ["Dle:Telemetry:Otlp:Enabled"] = "false",

            // The host's own start-up narration is not evidence of anything a contract test asserts,
            // and at the default level it buries the assertion message that is.
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft"] = "Warning",
        };

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);
        builder.ConfigureAppConfiguration(
            (_, configuration) => configuration.AddInMemoryCollection(Settings));

        // The host's own start-up narration is not evidence of anything a contract test asserts, and
        // at the default level it buries the assertion message that is.
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
    }
}
