using Microsoft.EntityFrameworkCore.Design;

namespace Dle.Persistence;

/// <summary>
/// Builds a <see cref="DleDbContext"/> for the EF Core command line tools.
/// </summary>
/// <remarks>
/// <para>
/// This project is a class library with no host, so <c>dotnet ef</c> has nothing to bootstrap from.
/// Without this factory the tools would have to start an application, which would mean a migration
/// could only be generated where a working configuration — and in practice a running database —
/// already exists. Generating a migration is a design-time activity and must not need either.
/// </para>
/// <para>
/// The connection string is read from <c>DLE_DESIGN_TIME_CONNECTION</c>, then from
/// <c>ConnectionStrings__Postgres</c>, and finally falls back to a local development default. None
/// of the three is contacted while scaffolding: EF Core only needs the provider to know how to
/// render SQL. The default is deliberately a localhost one with throwaway credentials, so that a
/// mistyped command cannot reach anything real.
/// </para>
/// </remarks>
public sealed class DleDesignTimeDbContextFactory : IDesignTimeDbContextFactory<DleDbContext>
{
    /// <summary>Environment variable naming the database to scaffold against.</summary>
    public const string ConnectionEnvironmentVariable = "DLE_DESIGN_TIME_CONNECTION";

    /// <summary>Standard .NET configuration override for the same connection string.</summary>
    public const string ConfigurationEnvironmentVariable = "ConnectionStrings__Postgres";

    /// <summary>Local development fallback, used when neither variable is set.</summary>
    public const string LocalDevelopmentConnection =
        "Host=localhost;Port=5432;Database=dle;Username=dle;Password=dle;Include Error Detail=true";

    /// <inheritdoc />
    public DleDbContext CreateDbContext(string[] args)
    {
        string connectionString =
            FirstNonEmpty(
                Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable),
                Environment.GetEnvironmentVariable(ConfigurationEnvironmentVariable))
            ?? LocalDevelopmentConnection;

        DbContextOptionsBuilder<DleDbContext> builder = new();
        builder.UseNpgsql(
            connectionString,
            npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(DleDbContext).Assembly.FullName);
                npgsql.MigrationsHistoryTable(new DlePersistenceOptions().MigrationsHistoryTable);
            });

        // The model is built here, and building it evaluates nothing that needs a tenant: the query
        // filters only read ITenantContext when a query executes. A context that throws on the
        // first tenant scoped query is exactly right for a tool that must never run one.
        return new DleDbContext(builder.Options, new AmbientTenantContext(), TimeProvider.System);
    }

    /// <summary>Returns the first value that is neither null nor blank.</summary>
    /// <param name="candidates">The values to consider, in order of precedence.</param>
    /// <returns>The first usable value, or <see langword="null"/>.</returns>
    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
