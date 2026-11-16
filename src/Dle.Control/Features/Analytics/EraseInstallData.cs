using System.Globalization;

using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Domain.Ports;

using Microsoft.AspNetCore.Http;

namespace Dle.Control.Features.Analytics;

/// <summary>
/// Response payload returned by <c>DELETE /api/v1/privacy/installs/{installId}</c>.
/// </summary>
public sealed record InstallErasureResponse
{
    /// <summary>Number of <c>installs</c> rows removed.</summary>
    public required int ErasedInstalls { get; init; }

    /// <summary>Number of <c>attributions</c> rows removed.</summary>
    public required int ErasedAttributions { get; init; }

    /// <summary>Number of <c>sdk_events</c> rows removed.</summary>
    public required int ErasedSdkEvents { get; init; }
}

/// <summary>
/// <c>DELETE /api/v1/privacy/installs/{installId}</c> — tenant-scoped GDPR Article 17 / DSAR
/// erasure of end-user installation records (§E.6.3).
/// </summary>
/// <remarks>
/// Deletes only rows within the authenticated caller's tenant. The immutable administrative audit
/// entry records only the tenant scope and row counts — never the end-user <c>install_id</c> — so
/// erasing personal data never reintroduces it into <c>audit_log</c>.
/// </remarks>
public static class EraseInstallData
{
    private const int MaxInstallIdLength = 256;

    /// <summary>Erases all stored data associated with one <paramref name="installId"/> in scope.</summary>
    /// <param name="installId">The SDK installation identifier supplied by the data subject.</param>
    /// <param name="context">The HTTP request, for authenticated tenant and operator identity.</param>
    /// <param name="store">Tenant-scoped privacy erasure store.</param>
    /// <param name="audit">Administrative audit writer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with erased counts per table, or 400 when the identifier is malformed.</returns>
    public static async Task<IResult> HandleAsync(
        string installId,
        HttpContext context,
        IPrivacyErasureStore store,
        IAuditLogWriter audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audit);

        if (string.IsNullOrWhiteSpace(installId))
        {
            return DleProblemResults.ValidationFailed("An install identifier is required.");
        }

        string normalized = installId.Trim();

        if (normalized.Length > MaxInstallIdLength || ContainsDisallowedCharacters(normalized))
        {
            return DleProblemResults.ValidationFailed(
                $"The install identifier must be at most {MaxInstallIdLength} printable characters with no whitespace or control characters.");
        }

        DleCaller caller = context.RequireDleCaller();

        InstallErasureResult result = await store.EraseInstallAsync(
            caller.TenantId,
            normalized,
            cancellationToken);

        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["erased_installs"] = result.ErasedInstalls.ToString(CultureInfo.InvariantCulture),
            ["erased_attributions"] = result.ErasedAttributions.ToString(CultureInfo.InvariantCulture),
            ["erased_sdk_events"] = result.ErasedSdkEvents.ToString(CultureInfo.InvariantCulture),
        };

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.PrivacyInstallErased,
            AuditActions.PrivacySubject,
            caller.TenantId.ToString(),
            metadata,
            cancellationToken);

        InstallErasureResponse response = new()
        {
            ErasedInstalls = result.ErasedInstalls,
            ErasedAttributions = result.ErasedAttributions,
            ErasedSdkEvents = result.ErasedSdkEvents,
        };

        return TypedResults.Json(response, AnalyticsJsonContext.Default.InstallErasureResponse);
    }

    private static bool ContainsDisallowedCharacters(string value)
    {
        foreach (char ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                return true;
            }
        }

        return false;
    }
}
