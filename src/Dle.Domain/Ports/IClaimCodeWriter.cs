using Dle.Domain.Entities;

namespace Dle.Domain.Ports;

/// <summary>
/// Writes issued claim codes to storage so they can be redeemed by the SDK upon install (FR-184).
/// </summary>
public interface IClaimCodeWriter
{
    /// <summary>Writes an issued claim code record.</summary>
    ValueTask WriteAsync(ClaimCodeRecord record, CancellationToken cancellationToken);
}
