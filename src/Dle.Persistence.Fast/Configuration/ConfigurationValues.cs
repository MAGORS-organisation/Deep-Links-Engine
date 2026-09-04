using Microsoft.Extensions.Configuration;

namespace Dle.Persistence.Fast.Configuration;

/// <summary>
/// Reads scalar configuration values without the reflection based configuration binder.
/// </summary>
/// <remarks>
/// <para>
/// <c>IConfiguration.Get&lt;T&gt;()</c> and <c>Bind()</c> resolve properties by reflection, which is
/// exactly what ADR-012 rules out for this assembly: the edge resolver is the component that is meant
/// to be publishable ahead of time, and one reflective binder in its composition root is enough to
/// close that door. Every value this module needs is a string, an integer or a boolean, so reading
/// them explicitly costs a few lines and keeps the module trimmable.
/// </para>
/// <para>
/// Parsing is invariant culture throughout (SHARED-KERNEL §0) and a malformed value is an error at
/// composition time rather than a silently substituted default: a mistyped pool size that halves
/// throughput is far more expensive to find in production than a failed start.
/// </para>
/// </remarks>
internal static class ConfigurationValues
{
    /// <summary>Reads a required non-empty string.</summary>
    internal static string RequireString(IConfiguration configuration, string key)
    {
        string? value = configuration[key];

        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Configuration value '{key}' is required and was not supplied.")
            : value;
    }

    /// <summary>Reads an optional string, mapping blank to <see langword="null"/>.</summary>
    internal static string? OptionalString(IConfiguration configuration, string key)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Reads an optional integer, falling back to <paramref name="fallback"/> when absent.</summary>
    internal static int Int32(IConfiguration configuration, string key, int fallback)
    {
        string? raw = configuration[key];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new InvalidOperationException($"Configuration value '{key}' is not a valid integer: '{raw}'.");
    }

    /// <summary>Reads an optional boolean, falling back to <paramref name="fallback"/> when absent.</summary>
    internal static bool Boolean(IConfiguration configuration, string key, bool fallback)
    {
        string? raw = configuration[key];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return bool.TryParse(raw, out bool value)
            ? value
            : throw new InvalidOperationException($"Configuration value '{key}' is not a valid boolean: '{raw}'.");
    }

    /// <summary>Throws when an integer option falls outside the range the module can work with.</summary>
    internal static int InRange(int value, int min, int max, string key)
    {
        return value >= min && value <= max
            ? value
            : throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Configuration value '{key}' must be between {min} and {max}; it was {value}."));
    }
}
