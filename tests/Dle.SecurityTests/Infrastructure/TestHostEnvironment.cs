namespace Dle.SecurityTests.Infrastructure;

/// <summary>
/// Serializes process-wide environment variable overrides across parallel
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> builds so that
/// composition roots reading <c>builder.Configuration</c> eagerly do not race with one another.
/// </summary>
internal static class TestHostEnvironment
{
    private static readonly Lock EnvironmentLock = new();

    public static TResult RunWithEnvironment<TResult>(
        IEnumerable<KeyValuePair<string, string?>> settings,
        Func<TResult> build)
    {
        List<KeyValuePair<string, string?>> applied = [.. settings];

        lock (EnvironmentLock)
        {
            Dictionary<string, string?> previous = new(StringComparer.Ordinal);

            try
            {
                foreach ((string key, string? value) in applied)
                {
                    string envKey = key.Replace(":", "__", StringComparison.Ordinal);
                    previous.TryAdd(envKey, Environment.GetEnvironmentVariable(envKey));
                    Environment.SetEnvironmentVariable(envKey, value);
                }

                return build();
            }
            finally
            {
                foreach ((string envKey, string? oldValue) in previous)
                {
                    Environment.SetEnvironmentVariable(envKey, oldValue);
                }
            }
        }
    }
}
