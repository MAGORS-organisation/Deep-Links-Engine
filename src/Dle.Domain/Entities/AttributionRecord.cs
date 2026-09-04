namespace Dle.Domain.Entities;

/// <summary>
/// The decision that tied an installation to a click. Maps to <c>attributions</c>.
/// </summary>
/// <remarks>
/// <para>
/// One installation has at most one attribution, and one click may be claimed by at most one
/// installation (TC-144). Both are enforced by unique indexes rather than by application
/// discipline, because a double claim is the cheapest attribution fraud there is.
/// </para>
/// <para>
/// <see cref="MatchType"/> and <see cref="Confidence"/> are always stored together. A
/// probabilistic match is never rounded up into a certainty, and <see cref="Evidence"/> records
/// what actually decided it — for a probabilistic match, which signals agreed and what each one
/// contributed. When a customer disputes an attribution, this column is the only defence
/// (§B.5.3).
/// </para>
/// </remarks>
public class AttributionRecord
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The installation, referencing <see cref="Install.Id"/>.</summary>
    public Guid InstallId { get; set; }

    /// <summary>Public identifier of the matched click.</summary>
    public string? ClickId { get; set; }

    /// <summary>Link the matched click belongs to.</summary>
    public long? LinkId { get; set; }

    /// <summary>Strategy that produced the match, stored as text: <c>install_referrer</c>,
    /// <c>login</c>, <c>claim_code</c>, <c>probabilistic</c>, <c>direct_open</c> or <c>none</c>.</summary>
    public string MatchType { get; set; } = "none";

    /// <summary>Confidence with two decimal places. Exactly <c>1.00</c> for a deterministic
    /// strategy.</summary>
    public decimal Confidence { get; set; }

    /// <summary>Instant of the decision, in UTC.</summary>
    public DateTimeOffset MatchedAt { get; set; }

    /// <summary>Width of the attribution window that was in force, in seconds. Stored with the
    /// record because the window is configurable and may change afterwards.</summary>
    public int? WindowSeconds { get; set; }

    /// <summary>What decided the match, stored as a JSON document.</summary>
    public string Evidence { get; set; } = "{}";
}
