namespace Dle.Domain.Entities;

/// <summary>
/// A keyed login reconciliation hint recorded when a signed-in visitor lands on a short link or
/// web destination, allowing S2 login attribution to run in <c>O(1)</c> instead of scanning raw
/// click partitions (FR-185). Maps to <c>click_login_hints</c>.
/// </summary>
public class ClickLoginHint
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Keyed HKDF-HMAC-SHA256 hash of the account identifier, produced by <c>LoginKeyHasher</c>.</summary>
    public byte[] LoginKeyHash { get; set; } = [];

    /// <summary>Click identifier the login hint stands for.</summary>
    public string ClickId { get; set; } = string.Empty;

    /// <summary>Link identifier the click or destination URL resolved to.</summary>
    public long? LinkId { get; set; }

    /// <summary>Optional application deep-link path resolved for the hint.</summary>
    public string? DeeplinkPath { get; set; }

    /// <summary>Instant the underlying click or signed-in web visit occurred, in UTC.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Instant this hint expires and becomes eligible for pruning, in UTC.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
