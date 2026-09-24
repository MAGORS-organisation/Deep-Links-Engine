namespace Dle.Domain.Entities;

/// <summary>
/// A campaign that groups links and supplies default UTM parameters (FR-108). Maps to
/// <c>campaigns</c>.
/// </summary>
public class Campaign
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Campaign name as shown in reports.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Default UTM parameters for links of the campaign, stored as a JSON document.</summary>
    public string Utm { get; set; } = "{}";

    /// <summary>Creation instant, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
