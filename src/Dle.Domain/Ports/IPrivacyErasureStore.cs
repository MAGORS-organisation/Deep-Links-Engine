namespace Dle.Domain.Ports;

/// <summary>
/// Counts of rows removed for a data-subject erasure request scoped to one tenant and installation.
/// </summary>
/// <param name="ErasedInstalls">Number of <c>installs</c> rows deleted.</param>
/// <param name="ErasedAttributions">Number of <c>attributions</c> rows deleted.</param>
/// <param name="ErasedSdkEvents">Number of <c>sdk_events</c> rows deleted.</param>
public sealed record InstallErasureResult(
    int ErasedInstalls,
    int ErasedAttributions,
    int ErasedSdkEvents);

/// <summary>
/// Tenant-scoped right-to-erasure store for an end-user <c>install_id</c> (§E.6.3).
/// </summary>
public interface IPrivacyErasureStore
{
    /// <summary>
    /// Atomically removes all attribution, installation, and SDK event records belonging to
    /// <paramref name="installId"/> within <paramref name="tenantId"/>.
    /// </summary>
    /// <param name="tenantId">The owning tenant identifier.</param>
    /// <param name="installId">The SDK installation identifier to erase.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Counts of rows removed per store table.</returns>
    Task<InstallErasureResult> EraseInstallAsync(
        Guid tenantId,
        string installId,
        CancellationToken cancellationToken);
}
