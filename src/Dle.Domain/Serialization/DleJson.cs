namespace Dle.Domain.Serialization;

/// <summary>
/// The two serializer configurations used across the product. Both instances are created once and
/// shared: <see cref="JsonSerializerOptions"/> caches its metadata, so a per call instance would
/// throw the cache away on every request.
/// </summary>
public static class DleJson
{
    /// <summary>
    /// The wire format for every DLE payload: snake_case property names, enums as snake_case
    /// strings, <see langword="null"/> values omitted when writing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reading is deliberately strict. Property matching is case sensitive, trailing commas and
    /// comments are rejected and the depth is capped at 32, so a hostile payload cannot exhaust the
    /// stack and a typo in a client integration fails loudly instead of silently dropping a field.
    /// </para>
    /// <para>
    /// Dictionary keys keep their original spelling: UTM parameters, query parameters and
    /// attribution evidence are data, not identifiers, and renaming them would corrupt them.
    /// </para>
    /// <para>
    /// On the hot path do not serialize through these options; pass the matching
    /// <see cref="DleDomainJsonContext"/> metadata instead, which avoids reflection entirely.
    /// </para>
    /// </remarks>
    public static JsonSerializerOptions Default { get; } = CreateDefault();

    /// <summary>
    /// Options for the Apple App Site Association and Android asset links documents.
    /// </summary>
    /// <remarks>
    /// These files have a schema imposed by Apple and Google whose keys are <c>"/"</c>, <c>"#"</c>,
    /// <c>"?"</c>, <c>"appIDs"</c> and <c>"sha256_cert_fingerprints"</c>. No naming policy is applied,
    /// so the literal names declared with <see cref="JsonPropertyNameAttribute"/> survive verbatim;
    /// a snake_case policy here would produce a file that both operating systems silently ignore.
    /// </remarks>
    public static JsonSerializerOptions WellKnown { get; } = CreateWellKnown();

    /// <summary>Builds the shared wire format options.</summary>
    private static JsonSerializerOptions CreateDefault()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            UnknownTypeHandling = JsonUnknownTypeHandling.JsonElement,
            WriteIndented = false,
            MaxDepth = MaxDocumentDepth,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));

        return options;
    }

    /// <summary>Builds the options for the well known documents.</summary>
    private static JsonSerializerOptions CreateWellKnown() => new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = false,
        MaxDepth = MaxDocumentDepth,
    };

    /// <summary>Nesting limit for every document the product reads or writes.</summary>
    private const int MaxDocumentDepth = 32;
}
