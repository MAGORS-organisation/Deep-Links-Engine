namespace Dle.Control.Features.Attribution;

/// <summary>
/// The tenant and application an authenticated SDK call belongs to.
/// </summary>
/// <param name="TenantId">Owning tenant. Every query the request makes is scoped to it.</param>
/// <param name="AppId">Application the key was issued for.</param>
/// <param name="SdkKeyId">The key row, recorded in evidence so a disputed attribution can be
/// traced back to the credential that produced it.</param>
public readonly record struct SdkCaller(Guid TenantId, Guid AppId, Guid SdkKeyId);
