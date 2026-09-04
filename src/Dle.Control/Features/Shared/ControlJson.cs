using System.Text.Json;

namespace Dle.Control.Features.Shared;

/// <summary>
/// Reads and writes the <c>jsonb</c> columns that hold shared-kernel shapes.
/// </summary>
/// <remarks>
/// <para>
/// Everything here goes through <see cref="DleDomainJsonContext"/>, the same source-generated
/// metadata the persistence layer and the edge read those columns with. That is not a style
/// preference: it is the only way the control plane can guarantee a stored document round trips.
/// If the API wrote a rule set through one serializer configuration and the edge read it through
/// another, a difference in enumeration spelling would turn a stored <c>app_or_store</c> action
/// into an unreadable one, and the edge answers an unreadable rule set with 404 (SHARED-KERNEL
/// §17.3, §13).
/// </para>
/// <para>
/// The HTTP wire format is a separate question and is configured once in
/// <c>AddDleControlCore</c>. A value therefore crosses two boundaries — request JSON to object,
/// object to column JSON — and each boundary is owned by exactly one configuration.
/// </para>
/// </remarks>
internal static class ControlJson
{
    /// <summary>An empty JSON object, the default of every document column.</summary>
    internal const string EmptyObject = "{}";

    /// <summary>An empty JSON array, the default of the routing rule column.</summary>
    internal const string EmptyArray = "[]";

    /// <summary>Renders a rule set for the <c>links.routing_rules</c> column.</summary>
    /// <param name="rules">The rules, in evaluation order.</param>
    /// <returns>The document to store.</returns>
    internal static string WriteRoutingRules(IReadOnlyList<RoutingRule>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return EmptyArray;
        }

        RoutingRule[] array = rules as RoutingRule[] ?? [.. rules];

        return JsonSerializer.Serialize(array, DleDomainJsonContext.Default.RoutingRuleArray);
    }

    /// <summary>Reads a rule set, treating a malformed document as no rules.</summary>
    /// <param name="json">The stored document.</param>
    /// <returns>The rules, never <see langword="null"/>.</returns>
    /// <remarks>
    /// A rule set that will not parse must not route anything, so the caller sees an empty set
    /// rather than a guess. The routing engine answers an empty set with
    /// <see cref="DecisionKind.NotFound"/>, which is the fail-closed outcome (SHARED-KERNEL §17.9).
    /// </remarks>
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
            return [];
        }
    }

    /// <summary>Renders Open Graph metadata for the <c>links.og_meta</c> column.</summary>
    /// <param name="og">The metadata, or <see langword="null"/>.</param>
    /// <returns>The document to store.</returns>
    internal static string WriteOgMeta(OgMeta? og) =>
        og is null
            ? EmptyObject
            : JsonSerializer.Serialize(og, DleDomainJsonContext.Default.OgMeta);

    /// <summary>Reads Open Graph metadata, treating a malformed document as none.</summary>
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

    /// <summary>Renders a flat string map, such as the UTM defaults of a link.</summary>
    /// <param name="map">The entries, or <see langword="null"/>.</param>
    /// <returns>The document to store.</returns>
    /// <remarks>
    /// Dictionary keys keep their spelling. UTM parameters are data rather than identifiers, and
    /// renaming <c>utm_Source</c> to <c>utm_source</c> on the way in would silently change what the
    /// customer's analytics receives.
    /// </remarks>
    internal static string WriteStringMap(IReadOnlyDictionary<string, string>? map)
    {
        if (map is null || map.Count == 0)
        {
            return EmptyObject;
        }

        Dictionary<string, string> materialized = map as Dictionary<string, string>
            ?? new Dictionary<string, string>(map, StringComparer.Ordinal);

        return JsonSerializer.Serialize(
            materialized,
            DleDomainJsonContext.Default.DictionaryStringString);
    }

    /// <summary>Reads a flat string map, treating a malformed document as empty.</summary>
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
