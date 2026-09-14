using Dle.Control.Features.Attribution;
using Dle.Domain.Entities;
using Dle.Domain.Privacy;
using Dle.Persistence.Repositories;

namespace Dle.UnitTests.Control;

/// <summary>
/// An in-memory stand-in for the attribution store that enforces the two uniqueness rules the real
/// schema enforces, because the use case under test delegates both of them to storage rather than
/// checking them itself.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>One attribution per installation — the unique index on
///   <c>attributions(install_id)</c> (TC-143).</description></item>
///   <item><description>One attribution per click — the partial unique index on
///   <c>attributions(click_id) WHERE click_id IS NOT NULL</c> (TC-144).</description></item>
/// </list>
/// Reproducing the constraints here is deliberate: the use case is being tested, not the database,
/// and the database's own behaviour belongs to the integration suite that needs a real Postgres.
/// </remarks>
internal sealed class FakeAttributionStore : IAttributionStore
{
    private readonly Dictionary<string, Install> _installs = new(StringComparer.Ordinal);
    private readonly List<AttributionRecord> _attributions = [];
    private readonly List<ClaimCodeRecord> _claimCodes = [];
    private readonly Dictionary<long, AttributedLink> _links = [];

    /// <summary>Consent mode the tenant is configured with.</summary>
    internal ConsentMode TenantConsentMode { get; set; } = ConsentMode.Full;

    /// <summary>Every attribution row that was written.</summary>
    internal IReadOnlyList<AttributionRecord> Attributions => _attributions;

    /// <summary>Every installation row that was created.</summary>
    internal IReadOnlyCollection<Install> Installs => _installs.Values;

    /// <summary>Claim codes that were marked as spent.</summary>
    internal List<Guid> ConsumedClaimCodes { get; } = [];

    /// <summary>Login key hashes that were recorded against an installation.</summary>
    internal List<(Guid InstallRowId, byte[] Hash)> LoginKeys { get; } = [];

    internal void AddLink(AttributedLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        _links[link.Id] = link;
    }

    internal void AddClaimCode(ClaimCodeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Id == Guid.Empty)
        {
            record.Id = Guid.NewGuid();
        }

        _claimCodes.Add(record);
    }

    public Task<ConsentMode> GetTenantConsentModeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(TenantConsentMode);

    public Task<Install> GetOrCreateInstallAsync(Install candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        string key = candidate.TenantId.ToString() + "/" + candidate.AppId.ToString() + "/" + candidate.InstallId;

        if (_installs.TryGetValue(key, out Install? existing))
        {
            return Task.FromResult(existing);
        }

        candidate.Id = Guid.NewGuid();
        _installs[key] = candidate;

        return Task.FromResult(candidate);
    }

    public Task RecordLoginKeyAsync(Guid installRowId, byte[] loginKeyHash, CancellationToken cancellationToken)
    {
        LoginKeys.Add((installRowId, loginKeyHash));
        return Task.CompletedTask;
    }

    public Task<AttributionRecord?> FindAttributionByInstallAsync(Guid installRowId, CancellationToken cancellationToken) =>
        Task.FromResult(_attributions.Find(record => record.InstallId == installRowId));

    public Task<AttributionWriteResult> TryCreateAttributionAsync(
        AttributionRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        AttributionRecord? forInstall = _attributions.Find(existing => existing.InstallId == record.InstallId);

        if (forInstall is not null)
        {
            return Task.FromResult(new AttributionWriteResult(AttributionOutcome.AlreadyAttributed, forInstall));
        }

        if (record.ClickId is { Length: > 0 } clickId &&
            _attributions.Exists(existing => string.Equals(existing.ClickId, clickId, StringComparison.Ordinal)))
        {
            return Task.FromResult(new AttributionWriteResult(AttributionOutcome.ClickAlreadyClaimed, Record: null));
        }

        record.Id = Guid.NewGuid();
        _attributions.Add(record);

        return Task.FromResult(new AttributionWriteResult(AttributionOutcome.Created, record));
    }

    public Task<AttributionWriteResult> TryUpgradeNoneAttributionAsync(
        AttributionRecord replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        AttributionRecord? stored = _attributions.Find(existing => existing.InstallId == replacement.InstallId);

        if (stored is null)
        {
            return TryCreateAttributionAsync(replacement, cancellationToken);
        }

        if (!string.Equals(stored.MatchType, "none", StringComparison.Ordinal))
        {
            return Task.FromResult(new AttributionWriteResult(AttributionOutcome.AlreadyAttributed, stored));
        }

        if (replacement.ClickId is { Length: > 0 } clickId &&
            _attributions.Exists(existing =>
                existing.Id != stored.Id &&
                string.Equals(existing.ClickId, clickId, StringComparison.Ordinal)))
        {
            return Task.FromResult(new AttributionWriteResult(AttributionOutcome.ClickAlreadyClaimed, stored));
        }

        stored.ClickId = replacement.ClickId;
        stored.LinkId = replacement.LinkId;
        stored.MatchType = replacement.MatchType;
        stored.Confidence = replacement.Confidence;
        stored.MatchedAt = replacement.MatchedAt;
        stored.WindowSeconds = replacement.WindowSeconds;
        stored.Evidence = replacement.Evidence;

        return Task.FromResult(new AttributionWriteResult(AttributionOutcome.Created, stored));
    }

    public Task<ClaimCodeRecord?> FindClaimCodeAsync(byte[] codeHash, CancellationToken cancellationToken) =>
        Task.FromResult(_claimCodes.Find(record => record.CodeHash.AsSpan().SequenceEqual(codeHash)));

    public Task ConsumeClaimCodeAsync(Guid claimCodeId, DateTimeOffset consumedAt, CancellationToken cancellationToken)
    {
        ConsumedClaimCodes.Add(claimCodeId);

        ClaimCodeRecord? record = _claimCodes.Find(candidate => candidate.Id == claimCodeId);

        if (record is not null)
        {
            record.ConsumedAt = consumedAt;
        }

        return Task.CompletedTask;
    }

    public Task<ClaimCodeRecord> AddClaimCodeAsync(ClaimCodeRecord record, CancellationToken cancellationToken)
    {
        AddClaimCode(record);
        return Task.FromResult(record);
    }

    public Task<AttributedLink?> FindLinkAsync(long linkId, CancellationToken cancellationToken) =>
        Task.FromResult(_links.GetValueOrDefault(linkId));

    public Task<AttributedLink?> FindLinkByHostAndSlugAsync(string host, string slug, CancellationToken cancellationToken) =>
        Task.FromResult<AttributedLink?>(null);
}
