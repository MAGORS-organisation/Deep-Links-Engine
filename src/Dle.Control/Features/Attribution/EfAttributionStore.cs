using System.Collections.ObjectModel;
using System.Text.Json;

using Dle.Domain.Attribution;
using Dle.Domain.Entities;
using Dle.Domain.Links;
using Dle.Domain.Ports;
using Dle.Domain.Privacy;
using Dle.Domain.Serialization;
using Dle.Persistence;
using Dle.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// The production <see cref="IAttributionStore"/>: EF Core for the control plane rows, the cached
/// link store for the one lookup that happens per reported open.
/// </summary>
/// <remarks>
/// The two integrity rules live in <see cref="AttributionRepository"/>, which owns the transaction
/// and the unique index violations that back them up. This class does not re-implement them; it
/// exists so the use cases can be exercised without a database and so that the link and claim code
/// reads sit behind the same seam as the writes.
/// </remarks>
public sealed partial class EfAttributionStore : IAttributionStore
{
    private const string DeletedTenantStatus = "deleted";

    private readonly DleDbContext _db;
    private readonly AttributionRepository _attributions;
    private readonly ILinkStore _links;
    private readonly ILogger<EfAttributionStore> _logger;

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="db">The control plane context.</param>
    /// <param name="attributions">Repository owning the attribution invariants.</param>
    /// <param name="links">Cached link lookup, used for reported opens.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public EfAttributionStore(
        DleDbContext db,
        AttributionRepository attributions,
        ILinkStore links,
        ILogger<EfAttributionStore> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(attributions);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _attributions = attributions;
        _links = links;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ConsentMode> GetTenantConsentModeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        string? mode = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId && t.Status != DeletedTenantStatus)
            .Select(t => t.ConsentMode)
            .FirstOrDefaultAsync(cancellationToken);

        return ParseConsentMode(mode);
    }

    /// <inheritdoc />
    public Task<Install> GetOrCreateInstallAsync(Install candidate, CancellationToken cancellationToken) =>
        _attributions.GetOrCreateInstallAsync(candidate, cancellationToken);

    /// <inheritdoc />
    public async Task RecordLoginKeyAsync(Guid installRowId, byte[] loginKeyHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loginKeyHash);

        Install? install = await _db.Installs
            .FirstOrDefaultAsync(i => i.Id == installRowId, cancellationToken);

        if (install is null || (install.LoginKeyHash is { } existing && existing.AsSpan().SequenceEqual(loginKeyHash)))
        {
            return;
        }

        install.LoginKeyHash = loginKeyHash;
        _ = await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<AttributionRecord?> FindAttributionByInstallAsync(Guid installRowId, CancellationToken cancellationToken) =>
        _attributions.FindByInstallAsync(installRowId, cancellationToken);

    /// <inheritdoc />
    public Task<AttributionWriteResult> TryCreateAttributionAsync(AttributionRecord record, CancellationToken cancellationToken) =>
        _attributions.TryCreateAsync(record, cancellationToken);

    /// <inheritdoc />
    public async Task<AttributionWriteResult> TryUpgradeNoneAttributionAsync(
        AttributionRecord replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        AttributionRecord? stored = await _db.Attributions
            .FirstOrDefaultAsync(a => a.InstallId == replacement.InstallId, cancellationToken);

        if (stored is null || !string.Equals(stored.MatchType, MatchTypeNames.None, StringComparison.Ordinal))
        {
            return new AttributionWriteResult(
                stored is null ? AttributionOutcome.ClickAlreadyClaimed : AttributionOutcome.AlreadyAttributed,
                stored);
        }

        if (replacement.ClickId is { Length: > 0 } clickId)
        {
            bool claimed = await _db.Attributions
                .AnyAsync(a => a.ClickId == clickId && a.InstallId != replacement.InstallId, cancellationToken);

            if (claimed)
            {
                return new AttributionWriteResult(AttributionOutcome.ClickAlreadyClaimed, Record: null);
            }
        }

        stored.ClickId = replacement.ClickId;
        stored.LinkId = replacement.LinkId;
        stored.MatchType = replacement.MatchType;
        stored.Confidence = replacement.Confidence;
        stored.MatchedAt = replacement.MatchedAt;
        stored.WindowSeconds = replacement.WindowSeconds;
        stored.Evidence = replacement.Evidence;

        _ = await _db.SaveChangesAsync(cancellationToken);

        return new AttributionWriteResult(AttributionOutcome.Created, stored);
    }

    /// <inheritdoc />
    public async Task<ClaimCodeRecord?> FindClaimCodeAsync(byte[] codeHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codeHash);

        return await _db.ClaimCodes
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CodeHash == codeHash, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ConsumeClaimCodeAsync(Guid claimCodeId, DateTimeOffset consumedAt, CancellationToken cancellationToken)
    {
        _ = await _db.ClaimCodes
            .Where(c => c.Id == claimCodeId && c.ConsumedAt == null)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.ConsumedAt, consumedAt), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ClaimCodeRecord> AddClaimCodeAsync(ClaimCodeRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        _ = _db.ClaimCodes.Add(record);
        _ = await _db.SaveChangesAsync(cancellationToken);

        return record;
    }

    /// <inheritdoc />
    public async Task<AttributedLink?> FindLinkAsync(long linkId, CancellationToken cancellationToken)
    {
        var row = await _db.Links
            .AsNoTracking()
            .Where(l => l.Id == linkId)
            .Select(l => new
            {
                l.Id,
                l.TenantId,
                l.DeeplinkPath,
                l.Title,
                l.Utm,
                Campaign = _db.Campaigns
                    .Where(c => c.Id == l.CampaignId)
                    .Select(c => c.Name)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new AttributedLink(
                row.Id,
                row.TenantId,
                row.DeeplinkPath,
                row.Campaign,
                row.Title,
                ReadParameters(row.Utm));
    }

    /// <inheritdoc />
    public async Task<AttributedLink?> FindLinkByHostAndSlugAsync(string host, string slug, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        LinkSnapshot? snapshot = await _links.FindAsync(host, slug, cancellationToken);

        if (snapshot is null)
        {
            return null;
        }

        string? campaign = snapshot.CampaignId is { } campaignId
            ? await _db.Campaigns
                .AsNoTracking()
                .Where(c => c.Id == campaignId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return new AttributedLink(
            snapshot.Id,
            snapshot.TenantId,
            snapshot.DeeplinkPath,
            campaign,
            snapshot.Title,
            snapshot.Utm);
    }

    /// <summary>Maps the stored consent mode text onto the enumeration, failing closed.</summary>
    private static ConsentMode ParseConsentMode(string? value) => value switch
    {
        "full" => ConsentMode.Full,
        "aggregate_only" => ConsentMode.AggregateOnly,
        _ => ConsentMode.Off,
    };

    /// <summary>
    /// Decodes the UTM document of a link. A document that is not a flat string map degrades to no
    /// parameters rather than failing the attribution; the match is still correct, only poorer.
    /// </summary>
    private ReadOnlyDictionary<string, string> ReadParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ReadOnlyDictionary<string, string>.Empty;
        }

        try
        {
            Dictionary<string, string>? values =
                JsonSerializer.Deserialize(json, DleDomainJsonContext.Default.DictionaryStringString);

            return values is null || values.Count == 0
                ? ReadOnlyDictionary<string, string>.Empty
                : new ReadOnlyDictionary<string, string>(values);
        }
        catch (JsonException exception)
        {
            LogMalformedUtm(_logger, exception);
            return ReadOnlyDictionary<string, string>.Empty;
        }
    }

    [LoggerMessage(
        EventId = 5201,
        Level = LogLevel.Warning,
        Message = "A links.utm document is not a flat string map and was ignored.")]
    private static partial void LogMalformedUtm(ILogger logger, Exception exception);
}
