namespace Dle.Domain.Entities;

/// <summary>
/// A key shipped inside a mobile application and used to authenticate SDK calls. Maps to
/// <c>sdk_keys</c>.
/// </summary>
/// <remarks>
/// An SDK key travels inside an application binary, so it is a public identifier rather than a
/// secret: anyone can extract it. Its value comes from being bound to one application and one
/// tenant and from being rate limited, not from being hidden. It must never be granted control
/// plane rights (§E.7, K6).
/// </remarks>
public class SdkKey
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Application the key belongs to.</summary>
    public Guid AppId { get; set; }

    /// <summary>Public prefix used for lookup.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Hash of the key material.</summary>
    public byte[] Hash { get; set; } = [];

    /// <summary>Whether the key is accepted.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Creation instant, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
