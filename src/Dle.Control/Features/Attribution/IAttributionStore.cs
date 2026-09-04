using Dle.Domain.Entities;
using Dle.Domain.Privacy;
using Dle.Persistence.Repositories;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// A link, reduced to what an attributed installation needs in order to navigate.
/// </summary>
/// <param name="Id">Link identifier.</param>
/// <param name="TenantId">Owning tenant. Compared before anything is returned, so a click that
/// belongs to another tenant becomes "no match" rather than a leak (SHARED-KERNEL §17.7).</param>
/// <param name="DeeplinkPath">Path the application should open.</param>
/// <param name="Campaign">Campaign name, when the link belongs to one.</param>
/// <param name="Title">Human readable link title.</param>
/// <param name="Parameters">Parameters handed to the application, typically the UTM set.</param>
public sealed record AttributedLink(
    long Id,
    Guid TenantId,
    string? DeeplinkPath,
    string? Campaign,
    string? Title,
    IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// Everything the attribution use cases persist or read, behind one seam.
/// </summary>
/// <remarks>
/// <para>
/// The seam exists so that the two integrity rules of §B.5.3 — one installation has at most one
/// attribution, one click is credited to at most one installation — can be exercised without a
/// database. They are enforced by unique indexes in the real implementation, and the fake used in
/// tests enforces exactly the same two rules, so a test that passes against the fake is testing the
/// decision logic rather than a mock's opinion of it.
/// </para>
/// <para>
/// Every method assumes the ambient tenant scope has already been entered. Nothing here takes a
/// tenant argument except the tenant's own configuration, because the tenant filter on the context
/// is what enforces isolation and passing it around would invite someone to pass the wrong one.
/// </para>
/// </remarks>
public interface IAttributionStore
{
    /// <summary>Reads the consent mode configured on a tenant (§E.6.2).</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The configured mode. An unknown or missing tenant yields
    /// <see cref="ConsentMode.Off"/>: the gate fails closed.</returns>
    Task<ConsentMode> GetTenantConsentModeAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the installation row, creating it on first sight (FR-188).
    /// </summary>
    /// <param name="candidate">The installation as reported by the SDK.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored installation. A repeated resolve finds the existing row, which is what
    /// makes the call idempotent (TC-143).</returns>
    Task<Install> GetOrCreateInstallAsync(Install candidate, CancellationToken cancellationToken);

    /// <summary>Records the account hash reported after a sign-in, for later reconciliation.</summary>
    /// <param name="installRowId">Primary key of the installation row.</param>
    /// <param name="loginKeyHash">Keyed hash of the account identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the value is stored.</returns>
    Task RecordLoginKeyAsync(Guid installRowId, byte[] loginKeyHash, CancellationToken cancellationToken);

    /// <summary>Reads the attribution of an installation.</summary>
    /// <param name="installRowId">Primary key of the installation row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The attribution, or <see langword="null"/> when the installation has none yet.</returns>
    Task<AttributionRecord?> FindAttributionByInstallAsync(Guid installRowId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes an attribution, honouring both uniqueness rules of §B.5.3.
    /// </summary>
    /// <param name="record">The attribution to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, and the attribution now in force.</returns>
    Task<AttributionWriteResult> TryCreateAttributionAsync(AttributionRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a stored <c>none</c> decision with a deterministic one, in place.
    /// </summary>
    /// <param name="replacement">The decision that should take its place. Its
    /// <see cref="AttributionRecord.InstallId"/> names the row to replace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="AttributionOutcome.Created"/> when the row was replaced,
    /// <see cref="AttributionOutcome.AlreadyAttributed"/> when the installation already carries a
    /// real match and must keep it, and <see cref="AttributionOutcome.ClickAlreadyClaimed"/> when
    /// the click is spoken for.
    /// </returns>
    /// <remarks>
    /// This is the narrow remedy TC-148 needs. A user whose claim code had expired asks for a new
    /// one and enters it; without this the stored "organic" verdict would be permanent and the new
    /// code would be pointless. It is deliberately the only mutation path: it never creates a second
    /// attribution, it only fires when the stored verdict is <c>none</c>, and the caller only offers
    /// it deterministic evidence.
    /// </remarks>
    Task<AttributionWriteResult> TryUpgradeNoneAttributionAsync(AttributionRecord replacement, CancellationToken cancellationToken);

    /// <summary>Finds an outstanding claim code by the keyed hash of the code.</summary>
    /// <param name="codeHash">Hash of the normalized code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record, or <see langword="null"/> when no such code was issued for this tenant.</returns>
    Task<ClaimCodeRecord?> FindClaimCodeAsync(byte[] codeHash, CancellationToken cancellationToken);

    /// <summary>Marks a claim code as spent.</summary>
    /// <param name="claimCodeId">The code row.</param>
    /// <param name="consumedAt">The instant of redemption.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the code can no longer be redeemed.</returns>
    Task ConsumeClaimCodeAsync(Guid claimCodeId, DateTimeOffset consumedAt, CancellationToken cancellationToken);

    /// <summary>Stores a newly issued claim code.</summary>
    /// <param name="record">The code row, carrying only the hash of the code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored row.</returns>
    Task<ClaimCodeRecord> AddClaimCodeAsync(ClaimCodeRecord record, CancellationToken cancellationToken);

    /// <summary>Reads the link an attribution points at.</summary>
    /// <param name="linkId">The link identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The link, or <see langword="null"/> when it does not exist in this tenant.</returns>
    Task<AttributedLink?> FindLinkAsync(long linkId, CancellationToken cancellationToken);

    /// <summary>Resolves the URL reported by a <c>link_open</c> event to one of this tenant's links.</summary>
    /// <param name="host">Normalized host.</param>
    /// <param name="slug">Normalized slug.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The link, or <see langword="null"/> when the URL is not ours or belongs to another
    /// tenant (§B.6.4, FR-223).</returns>
    Task<AttributedLink?> FindLinkByHostAndSlugAsync(string host, string slug, CancellationToken cancellationToken);
}
