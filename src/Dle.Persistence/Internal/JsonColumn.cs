using System.Text.Json;

using Dle.Domain.Links;
using Dle.Domain.Routing;
using Dle.Domain.Serialization;

namespace Dle.Persistence.Internal;

/// <summary>
/// Reads the <c>jsonb</c> columns that hold shared-kernel shapes.
/// </summary>
/// <remarks>
/// The entities keep these columns as strings on purpose: the control plane stores and returns them
/// verbatim most of the time, and round tripping them through an object model would reformat
/// documents nobody asked to reformat. Where a typed value is genuinely needed — building a
/// <see cref="LinkSnapshot"/>, say — it is produced here, through the source generated context, so
/// the same code path works when the edge is compiled ahead of time.
/// </remarks>
internal static class JsonColumn
{
    /// <summary>Reads a routing rule array, treating malformed content as no rules.</summary>
    /// <param name="json">The stored document.</param>
    /// <returns>The rules, never <see langword="null"/>.</returns>
    internal static IReadOnlyList<RoutingRule> ReadRoutingRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, DleDomainJsonContext.Default.RoutingRuleArray) ?? [];
        }
        catch (JsonException)
        {
            // A rule set that will not parse must not route anything. The caller sees an empty set,
            // which the routing engine answers with NotFound rather than with a guess.
            return [];
        }
    }

    /// <summary>Reads Open Graph metadata, treating malformed content as none.</summary>
    /// <param name="json">The stored document.</param>
    /// <returns>The metadata, never <see langword="null"/>.</returns>
    internal static OgMeta ReadOgMeta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return OgMeta.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize(json, DleDomainJsonContext.Default.OgMeta) ?? OgMeta.Empty;
        }
        catch (JsonException)
        {
            return OgMeta.Empty;
        }
    }

    /// <summary>Reads a flat string dictionary such as the UTM defaults of a link.</summary>
    /// <param name="json">The stored document.</param>
    /// <returns>The entries, never <see langword="null"/>.</returns>
    internal static IReadOnlyDictionary<string, string> ReadStringMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize(json, DleDomainJsonContext.Default.DictionaryStringString)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
