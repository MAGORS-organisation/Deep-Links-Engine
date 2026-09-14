using System.Text.Json;

using Dle.Control.Identity;

namespace Dle.Control.Features.Shared;

/// <summary>
/// The action names written to the administrative audit trail (FR-246).
/// </summary>
/// <remarks>
/// Constants rather than interpolated strings, because these values are queried: an operator asking
/// "who changed this domain last quarter" filters on them, and a typo in one handler would make an
/// operation invisible to that query without failing anything.
/// </remarks>
public static class AuditActions
{
    /// <summary>A link was created.</summary>
    public const string LinkCreated = "link.created";

    /// <summary>A link was edited.</summary>
    public const string LinkUpdated = "link.updated";

    /// <summary>A link was archived, meaning switched off but kept.</summary>
    public const string LinkArchived = "link.archived";

    /// <summary>A link was deleted outright.</summary>
    public const string LinkDeleted = "link.deleted";

    /// <summary>A batch of links was imported.</summary>
    public const string LinkBulkImported = "link.bulk_imported";

    /// <summary>A link template was created.</summary>
    public const string TemplateCreated = "template.created";

    /// <summary>A link template was edited.</summary>
    public const string TemplateUpdated = "template.updated";

    /// <summary>A link template was deleted.</summary>
    public const string TemplateDeleted = "template.deleted";

    /// <summary>A domain was registered.</summary>
    public const string DomainCreated = "domain.created";

    /// <summary>A domain was edited.</summary>
    public const string DomainUpdated = "domain.updated";

    /// <summary>A domain was removed.</summary>
    public const string DomainDeleted = "domain.deleted";

    /// <summary>A domain verification run was executed.</summary>
    public const string DomainVerified = "domain.verified";

    /// <summary>An application was registered.</summary>
    public const string AppCreated = "app.created";

    /// <summary>An application was edited.</summary>
    public const string AppUpdated = "app.updated";

    /// <summary>An application was removed.</summary>
    public const string AppDeleted = "app.deleted";

    /// <summary>A key embedded in a customer application was issued.</summary>
    public const string SdkKeyCreated = "sdk_key.created";

    /// <summary>A key embedded in a customer application was revoked.</summary>
    public const string SdkKeyRevoked = "sdk_key.revoked";

    /// <summary>A tenant was provisioned.</summary>
    public const string TenantCreated = "tenant.created";

    /// <summary>A tenant was edited.</summary>
    public const string TenantUpdated = "tenant.updated";

    /// <summary>A tenant was deactivated.</summary>
    public const string TenantDeleted = "tenant.deleted";

    /// <summary>A control-plane API key was issued.</summary>
    public const string ApiKeyCreated = "api_key.created";

    /// <summary>A control-plane API key was revoked.</summary>
    public const string ApiKeyRevoked = "api_key.revoked";

    /// <summary>Subject kind of a link.</summary>
    public const string LinkSubject = "link";

    /// <summary>Subject kind of a link template, which is stored as a campaign.</summary>
    public const string TemplateSubject = "template";

    /// <summary>Subject kind of a domain.</summary>
    public const string DomainSubject = "domain";

    /// <summary>Subject kind of an application.</summary>
    public const string AppSubject = "app";

    /// <summary>Subject kind of a key embedded in a customer application.</summary>
    public const string SdkKeySubject = "sdk_key";

    /// <summary>Subject kind of a tenant.</summary>
    public const string TenantSubject = "tenant";

    /// <summary>Subject kind of a control-plane API key.</summary>
    public const string ApiKeySubject = "api_key";
}

/// <summary>
/// Writes one audit entry per administrative operation (FR-246).
/// </summary>
/// <remarks>
/// <para>
/// The entry records four things and only four: which operator acted, what they did, which
/// configuration object they did it to, and when. There is deliberately nowhere in it for an end
/// user identifier — no click identifier, no install identifier, no address, no user agent. That
/// shape is what keeps an append-only trail compatible with the right to erasure under article 17
/// of the GDPR: the two requirements only contradict each other when the immutable record contains
/// data about a data subject, and here it structurally cannot. An erasure request therefore only
/// ever reaches the click stream and the attributions, where no retention obligation stands in its
/// way (§E.6.3, FR-246).
/// </para>
/// <para>
/// <see cref="AuditLogWriter"/> enforces the same rule again on the way into the database, refusing
/// metadata that carries a forbidden key. Two checks for one invariant is not redundancy: this one
/// keeps the call sites honest, that one keeps the table honest.
/// </para>
/// </remarks>
internal static class AdministrativeAudit
{
    /// <summary>Records an administrative operation for the calling operator.</summary>
    /// <param name="audit">The audit trail writer.</param>
    /// <param name="caller">The authenticated caller.</param>
    /// <param name="action">What was done, from <see cref="AuditActions"/>.</param>
    /// <param name="subjectType">Kind of object acted upon.</param>
    /// <param name="subjectId">Identifier of that object, as text.</param>
    /// <param name="metadata">
    /// Configuration detail of the change, such as which fields were replaced. Never anything
    /// derived from an end user.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the entry has been written.</returns>
    internal static async Task RecordAsync(
        AuditLogWriter audit,
        DleCaller caller,
        string action,
        string subjectType,
        string subjectId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(caller);

        await audit.WriteAsync(
            action,
            subjectType,
            subjectId,
            caller.ActorType,
            caller.ActorId,
            Render(metadata),
            cancellationToken);
    }

    /// <summary>Renders the metadata document.</summary>
    /// <param name="metadata">The entries, or <see langword="null"/>.</param>
    /// <returns>A JSON object, never <see langword="null"/>.</returns>
    private static string Render(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return ControlJson.EmptyObject;
        }

        Dictionary<string, string> materialized = metadata as Dictionary<string, string>
            ?? new Dictionary<string, string>(metadata, StringComparer.Ordinal);

        return JsonSerializer.Serialize(
            materialized,
            DleDomainJsonContext.Default.DictionaryStringString);
    }
}
