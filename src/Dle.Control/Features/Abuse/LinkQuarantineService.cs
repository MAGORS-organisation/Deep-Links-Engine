using System.Globalization;
using System.Text.Json;

using Dle.Control.Features.Shared;
using Dle.Control.Features.Webhooks;
using Dle.Domain.Ports;
using Dle.Domain.Serialization;
using Dle.Persistence.Repositories;
using Dle.Persistence.Tenancy;

using Microsoft.Extensions.Logging;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// Withdraws a link from service and returns it, with the trail both actions have to leave
/// (TC-103, §E.3 step 8).
/// </summary>
/// <remarks>
/// <para>
/// Quarantine, never deletion. A withdrawn link keeps its row and answers 410 Gone with an
/// explanation; a deleted one answers 404 and is indistinguishable from a slug that never existed.
/// The difference is the whole point: when a complaint about the same link arrives three months
/// later — from the reporter, from a registrar, from a regulator — the row, its timestamps and its
/// audit entries are the only way to answer it. Quiet removal destroys exactly the evidence the
/// operator will be asked for.
/// </para>
/// <para>
/// One service rather than a handler, because the same decision is taken by a person in the triage
/// queue and by the nightly re-check that finds a target has turned malicious. They must record the
/// same things and notify the same people; the only difference is the actor.
/// </para>
/// </remarks>
public sealed class LinkQuarantineService
{
    /// <summary>Action name recorded in the audit trail when a link is withdrawn.</summary>
    public const string QuarantineAction = "link.quarantined";

    /// <summary>Action name recorded in the audit trail when a link is returned to service.</summary>
    public const string ReleaseAction = "link.released";

    private readonly AbuseRepository _reports;
    private readonly AuditLogWriter _audit;
    private readonly IWebhookOutbox _outbox;
    private readonly ITenantContext _tenantContext;
    private readonly AbuseMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LinkQuarantineService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="reports">Report and quarantine storage.</param>
    /// <param name="audit">Audit trail.</param>
    /// <param name="outbox">Transactional webhook outbox.</param>
    /// <param name="tenantContext">Tenant scope.</param>
    /// <param name="metrics">Instruments.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public LinkQuarantineService(
        AbuseRepository reports,
        AuditLogWriter audit,
        IWebhookOutbox outbox,
        ITenantContext tenantContext,
        AbuseMetrics metrics,
        TimeProvider timeProvider,
        ILogger<LinkQuarantineService> logger)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _reports = reports;
        _audit = audit;
        _outbox = outbox;
        _tenantContext = tenantContext;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Withdraws a link from service.</summary>
    /// <param name="link">The link, already located across tenants.</param>
    /// <param name="reason">Why it is being withdrawn, recorded and sent to the tenant.</param>
    /// <param name="actor">Who decided.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when this call withdrew it, <see langword="false"/> when it was
    /// already withdrawn. Repeating the action is not an error: two operators reaching the same
    /// conclusion about the same link is a normal Tuesday.
    /// </returns>
    internal async Task<bool> QuarantineAsync(
        LocatedLink link,
        string reason,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        bool changed = await _reports.QuarantineLinkAsync(link.LinkId, cancellationToken);

        if (!changed)
        {
            return false;
        }

        await RecordAsync(
            link,
            QuarantineAction,
            WebhookEventTypes.LinkQuarantined,
            reason,
            actor,
            cancellationToken);

        _metrics.QuarantineDecision("quarantine", actor.ActorId is null ? "system" : "operator");
        _metrics.Blocked("manual", "blocked");

        _logger.LogWarning(
            "Link {LinkId} was withdrawn from service. Reason: {Reason}.",
            link.LinkId,
            reason);

        return true;
    }

    /// <summary>Returns a withdrawn link to service.</summary>
    /// <param name="link">The link, already located across tenants.</param>
    /// <param name="reason">Why the withdrawal is being lifted.</param>
    /// <param name="actor">Who decided.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when this call released it.</returns>
    internal async Task<bool> ReleaseAsync(
        LocatedLink link,
        string reason,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        bool changed = await _reports.ReleaseLinkAsync(link.LinkId, cancellationToken);

        if (!changed)
        {
            return false;
        }

        await RecordAsync(
            link,
            ReleaseAction,
            WebhookEventTypes.LinkReleased,
            reason,
            actor,
            cancellationToken);

        _metrics.QuarantineDecision("release", actor.ActorId is null ? "system" : "operator");

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Link {LinkId} was returned to service. Reason: {Reason}.",
                link.LinkId,
                reason);
        }

        return true;
    }

    /// <summary>
    /// Writes the audit entry and the outbound event, both inside the owning tenant's scope.
    /// </summary>
    /// <param name="link">The link that was acted on.</param>
    /// <param name="action">Audit action name.</param>
    /// <param name="eventType">Webhook event type.</param>
    /// <param name="reason">The stated reason.</param>
    /// <param name="actor">Who decided.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when both are written.</returns>
    /// <remarks>
    /// The tenant is told. A link disappearing from service without a word is how a customer finds
    /// out from their own users, which is the worst possible way — and under the notice and action
    /// mechanism the affected party is entitled to know that a decision was taken and on what
    /// ground.
    /// </remarks>
    private async Task RecordAsync(
        LocatedLink link,
        string action,
        string eventType,
        string reason,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        string linkId = link.LinkId.ToString(CultureInfo.InvariantCulture);

        using (_tenantContext.BeginScope(link.TenantId))
        {
            Dictionary<string, string> metadata = new(StringComparer.Ordinal)
            {
                ["reason"] = reason,
                ["link_id"] = linkId,
            };

            await _audit.WriteAsync(
                action,
                "link",
                linkId,
                actor.ActorType,
                actor.ActorId,
                JsonSerializer.Serialize(metadata, DleDomainJsonContext.Default.DictionaryStringString),
                cancellationToken);
        }

        Dictionary<string, string> data = new(StringComparer.Ordinal)
        {
            ["link_id"] = linkId,
            ["reason"] = reason,
            ["decided_at"] = now.ToString("O", CultureInfo.InvariantCulture),
        };

        await _outbox.EnqueueAsync(
            link.TenantId,
            eventType,
            WebhookEventTypes.Render(eventType, Guid.CreateVersion7(now), now, data),
            cancellationToken);
    }
}
