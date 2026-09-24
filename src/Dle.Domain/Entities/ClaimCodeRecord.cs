namespace Dle.Domain.Entities;

/// <summary>
/// A claim code issued on an interstitial page and redeemable inside the application (FR-184).
/// Maps to <c>claim_codes</c>.
/// </summary>
/// <remarks>
/// Only the hash of the code is stored: the code is short and low entropy, so a leaked table must
/// not hand an attacker a set of valid claims. <see cref="ConsumedAt"/> makes redemption single
/// use, and the short expiry bounds the guessing window (§E.4.1, K3; TC-148).
/// </remarks>
public class ClaimCodeRecord
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Hash of the normalized code. Compared in constant time.</summary>
    public byte[] CodeHash { get; set; } = [];

    /// <summary>Click the code stands for.</summary>
    public string? ClickId { get; set; }

    /// <summary>Link the click belongs to.</summary>
    public long? LinkId { get; set; }

    /// <summary>Expiry instant, in UTC. One hour after issue by default.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Instant the code was redeemed, in UTC. A non null value makes it unusable.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>Creation instant, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
