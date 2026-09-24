namespace Dle.SecurityTests.Infrastructure;

/// <summary>
/// The edge host shared by the tests that are not about a rate limit.
/// </summary>
/// <remarks>
/// The two budgets of §E.9 are process-wide and outlive a request, so this host raises them far out of
/// the way. That is not a weakening of anything: the tests that share it make thousands of requests
/// from a handful of addresses in order to assert properties of the <em>response</em>, and if the
/// enumeration guard fired halfway through, half of those assertions would be examining a shadow-banned
/// 404 instead of the answer they meant to examine. The limits themselves are asserted in
/// <c>Enumeration</c>, against a host built for that purpose with the §E.9 defaults intact.
/// </remarks>
public sealed class EdgeFixture : IAsyncLifetime
{
    /// <summary>The running host.</summary>
    public EdgeApiFactory Factory { get; } = new();

    /// <inheritdoc />
    public ValueTask InitializeAsync()
    {
        Factory.Overrides["Dle:RateLimits:Edge:Enabled"] = "false";
        Factory.Overrides["Dle:RateLimits:Edge:NotFound:TokensPerPeriod"] = "100000";
        Factory.Overrides["Dle:RateLimits:Edge:NotFound:Burst"] = "100000";

        // Touching the server forces the host to build, so a configuration mistake surfaces here rather
        // than inside the first assertion that happens to run.
        _ = Factory.Server;

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Factory.Dispose();

        return ValueTask.CompletedTask;
    }
}

/// <summary>Marks the tests that share the edge host.</summary>
[CollectionDefinition(Name)]
#pragma warning disable CA1711 // The xUnit collection marker is named for what it marks.
public sealed class EdgeCollection : ICollectionFixture<EdgeFixture>
{
    /// <summary>Name of the collection.</summary>
    public const string Name = "edge";
}
#pragma warning restore CA1711
