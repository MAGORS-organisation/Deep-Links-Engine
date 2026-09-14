namespace Dle.Domain.WellKnown;

/// <summary>
/// One entry of Apple's modern <c>components</c> array (§A.2.1) — and, verbatim, one entry of Android's
/// <c>dynamic_app_link_components</c> array (§A.2.2, FR-142).
/// </summary>
/// <remarks>
/// <para>
/// The JSON keys are single punctuation characters (<c>"/"</c> for the path, <c>"#"</c> for the
/// fragment, <c>"?"</c> for the query) and are fixed by Apple. They are pinned here with
/// <see cref="JsonPropertyNameAttribute"/> so that no serializer naming policy can rewrite them.
/// </para>
/// <para>
/// Android 15+ reuses exactly this shape. Writing Android's older <c>pathPattern</c> notation into
/// <c>dynamic_app_link_components</c> instead is a common mistake that fails silently — the file
/// validates, the links simply never open the application (§C.3.3).
/// </para>
/// </remarks>
public sealed record AasaComponent
{
    /// <summary>Path pattern, for example <c>/promo/*</c>. Serialized under the key <c>"/"</c>.</summary>
    [JsonPropertyName("/")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    /// <summary>Fragment pattern, for example <c>no_dl</c>. Serialized under the key <c>"#"</c>.</summary>
    [JsonPropertyName("#")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Fragment { get; init; }

    /// <summary>Query parameter patterns. Serialized under the key <c>"?"</c>.</summary>
    [JsonPropertyName("?")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Query { get; init; }

    /// <summary>When <see langword="true"/>, a match on this component means the link must not open the application.</summary>
    [JsonPropertyName("exclude")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Exclude { get; init; }

    /// <summary>Free-form note. Apple ignores it; it exists so a human can tell why a component is there.</summary>
    [JsonPropertyName("comment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Comment { get; init; }

    /// <summary>Whether the pattern is matched case sensitively. Apple's default is <see langword="true"/>.</summary>
    [JsonPropertyName("caseSensitive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CaseSensitive { get; init; }

    /// <summary>Whether the pattern is compared against the percent-encoded form of the URL.</summary>
    [JsonPropertyName("percentEncoded")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PercentEncoded { get; init; }
}
