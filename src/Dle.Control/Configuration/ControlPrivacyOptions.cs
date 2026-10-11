using System.ComponentModel.DataAnnotations;

namespace Dle.Control.Configuration;

/// <summary>
/// Privacy and consent options bound from <c>Dle:Privacy</c> (§C.4, §E.6.2).
/// </summary>
public sealed class ControlPrivacyOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Dle:Privacy";

    /// <summary>
    /// Default consent mode assigned to a freshly provisioned tenant when not specified in the request.
    /// One of <c>off</c>, <c>aggregate_only</c> or <c>full</c>.
    /// </summary>
    [Required]
    [RegularExpression("^(off|aggregate_only|full)$")]
    public string ConsentMode { get; set; } = "aggregate_only";
}
