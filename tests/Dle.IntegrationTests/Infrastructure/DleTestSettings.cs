namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// The configuration the two hosts are started with under test.
/// </summary>
/// <remarks>
/// <para>
/// Written out in full rather than inherited from the shipped <c>appsettings.json</c>, so that a
/// test run does not depend on which file happened to be copied next to the test binary and so that
/// every value a test relies on is visible in one place. The values that differ from production are
/// listed with the reason, because "the test config turned that off" is otherwise the hardest kind
/// of false pass to find.
/// </para>
/// <para>
/// The master secret is a fixed string. Determinism is the point: the click identifier codec, the
/// slug permutation and the address hasher all derive from it, so a fixed secret is what lets a test
/// mint a click identifier in one process and decode it in another (TC-141).
/// </para>
/// </remarks>
public static class DleTestSettings
{
    /// <summary>
    /// The deployment-wide secret every derived key hangs off. Fixed so that the click identifier a
    /// test mints can be decoded by the host under test.
    /// </summary>
    public const string MasterSecret = "dle-integration-tests-master-secret-0123456789";

    /// <summary>Base settings shared by both hosts.</summary>
    /// <param name="postgres">Connection string for the test database.</param>
    /// <param name="valkey">Connection string for the L2 cache, or empty for L1 only.</param>
    /// <returns>A mutable dictionary the caller can add to.</returns>
    public static Dictionary<string, string?> Common(string postgres, string valkey)
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AllowedHosts"] = "*",
            ["ConnectionStrings:Postgres"] = postgres,
            ["ConnectionStrings:PostgresRead"] = string.Empty,
            ["ConnectionStrings:Valkey"] = valkey,
            ["ConnectionStrings:ClickHouse"] = string.Empty,

            ["Dle:Crypto:MasterSecret"] = MasterSecret,
            ["Dle:Crypto:SigningAlgorithm"] = "HS256",
            ["Dle:Crypto:HybridPqEnabled"] = "false",
            ["Dle:Crypto:SlugFeistelRounds"] = "4",
            ["Dle:Crypto:ApiKeyPrefix"] = "dle",

            // hash_only is the shipped default and would leave ip_prefix null everywhere. The
            // attribution and partition tests need the prefix to exist, and the consent gate still
            // has the final say on whether it is written (SHARED-KERNEL section 2).
            ["Dle:Privacy:IpStorage"] = "full",
            ["Dle:Privacy:ConsentMode"] = "aggregate_only",

            ["Dle:Telemetry:OtlpEndpoint"] = string.Empty,
            ["Dle:Telemetry:TraceSampleRatio"] = "0",

            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning",
        };
    }

    /// <summary>Settings for the edge data plane.</summary>
    /// <param name="postgres">Connection string for the test database.</param>
    /// <param name="valkey">Connection string for the L2 cache, or empty for L1 only.</param>
    /// <returns>A mutable dictionary the caller can add to.</returns>
    public static Dictionary<string, string?> Edge(string postgres, string valkey)
    {
        Dictionary<string, string?> settings = Common(postgres, valkey);

        // No GeoIP database is shipped with the tests. country stays null, geo predicates fall
        // through to the default rule, and dle_geoip_available reports 0 (section D.6).
        settings["Dle:Edge:GeoIp:Provider"] = "None";
        settings["Dle:Edge:GeoIp:AutoUpdate"] = "false";

        // A closed test network has no reverse DNS, so confirming a crawler by PTR would turn every
        // recognised crawler into a spoof. The crawlers TC-106 exercises publish no DNS convention
        // and are accepted without a lookup either way; this only keeps the search engines out of a
        // resolver timeout. TC-107 belongs to the unit suite, where the verifier is substituted.
        settings["Dle:Edge:BotDetection:ReverseDnsVerify"] = "false";

        settings["Dle:Edge:Interstitial:Enabled"] = "true";
        settings["Dle:Edge:Interstitial:Branding"] = "Tenant";
        settings["Dle:Edge:Interstitial:DefaultLanguage"] = "en";

        settings["Dle:Edge:Cache:L1Seconds"] = "30";
        settings["Dle:Edge:Cache:L2Minutes"] = "10";
        settings["Dle:Edge:Cache:NegativeSeconds"] = "15";

        settings["Dle:Edge:Network:UseForwardedHeaders"] = "false";

        // The enumeration budget is per network prefix and every request from the test client shares
        // one, so the shipped burst of forty would shadow ban the suite partway through a run
        // (section E.9). The mechanism itself is asserted in the security suite, where the budget is
        // the thing under test rather than an obstacle to it.
        settings["Dle:RateLimits:Edge:Enabled"] = "true";
        settings["Dle:RateLimits:Edge:Resolve:PermitsPerWindow"] = "1000000";
        settings["Dle:RateLimits:Edge:NotFound:TokensPerPeriod"] = "100000";
        settings["Dle:RateLimits:Edge:NotFound:Burst"] = "100000";
        settings["Dle:RateLimits:Edge:Qr:PermitsPerWindow"] = "100000";

        settings["Dle:Persistence:Fast:ApplicationName"] = "dle-edge-tests";
        settings["Dle:Persistence:Fast:MinPoolSize"] = "0";

        return settings;
    }

    /// <summary>Settings for the control plane.</summary>
    /// <param name="postgres">Connection string for the test database.</param>
    /// <param name="valkey">Connection string for the L2 cache, or empty for L1 only.</param>
    /// <returns>A mutable dictionary the caller can add to.</returns>
    public static Dictionary<string, string?> Control(string postgres, string valkey)
    {
        Dictionary<string, string?> settings = Common(postgres, valkey);

        // No administration bundle is published into the test output, and MapFallback would
        // otherwise answer every unmatched path with a problem document about a missing index page.
        settings["Dle:Control:ServeAdminSpa"] = "false";
        settings["Dle:Control:PublicScheme"] = "https";
        settings["Dle:Control:NodeId"] = "0";

        // The scheduled workers would otherwise start fetching association files and rolling up
        // analytics against the test database while the tests are asserting on it.
        settings["Dle:Workers:Enabled"] = "false";

        settings["Dle:Analytics:Provider"] = "postgres";

        settings["Dle:Identity:EnableSdkKeys"] = "true";
        settings["Dle:Identity:SdkKeyName"] = "dlk";
        settings["Dle:Identity:CredentialCacheSeconds"] = "60";

        settings["Dle:Attribution:Strategies:0"] = "install_referrer";
        settings["Dle:Attribution:Strategies:1"] = "login";
        settings["Dle:Attribution:Strategies:2"] = "claim_code";
        settings["Dle:Attribution:Probabilistic:Enabled"] = "false";

        settings["Dle:Abuse:UrlHausEnabled"] = "false";

        settings["Dle:RateLimits:ReadPerMinute"] = "1000000";
        settings["Dle:RateLimits:WritePerMinute"] = "1000000";
        settings["Dle:RateLimits:LinkCreatePerMinute"] = "100000";
        settings["Dle:RateLimits:NewTenantLinkCreatePerMinute"] = "100000";
        settings["Dle:RateLimits:AuthAttemptsPerMinute"] = "10000";
        settings["Dle:RateLimits:AuthAttemptBurst"] = "10000";

        settings["Dle:Persistence:Fast:ApplicationName"] = "dle-control-tests";
        settings["Dle:Persistence:Fast:MinPoolSize"] = "0";

        return settings;
    }
}
