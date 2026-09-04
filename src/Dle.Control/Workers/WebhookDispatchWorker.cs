using System.Security.Cryptography;

using Dle.Control.Features.Webhooks;
using Dle.Domain.Entities;
using Dle.Persistence.Repositories;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Workers;

/// <summary>
/// Drains the webhook outbox: signs, delivers, retries, dead letters (§B.3 component C-10, FR-204).
/// </summary>
/// <remarks>
/// <para>
/// The outbox is a table rather than a queue for one reason worth restating: a postback must not be
/// promised for an event whose transaction later rolls back. The delivery row is written in the same
/// transaction as the change that caused it, and this worker is the only thing that turns such a row
/// into an HTTP request.
/// </para>
/// <para>
/// Backoff is exponential with jitter and a hard bound. The exponential part is what lets a customer
/// endpoint be down for a few minutes without losing anything; the bound is what stops a customer
/// endpoint that is down for two days from holding the queue; the jitter is what stops every tenant's
/// retry landing on a shared endpoint at the same instant, which without it is a herd the engine
/// creates itself.
/// </para>
/// <para>
/// What cannot be delivered is dead lettered, counted, and left in the table. It is not deleted and
/// it is not retried forever. <c>dle_webhook_dlq_size</c> is published every pass so that the
/// failure is loud: every row in that queue is data the customer was promised and never received,
/// and a silent one is worse than none at all (§C.6).
/// </para>
/// </remarks>
public sealed partial class WebhookDispatchWorker : LeaderElectedBackgroundService
{
    /// <summary>Job name, used as the advisory lock key and the metric tag.</summary>
    public const string Job = "dle.worker.webhook-dispatch";

    private readonly IServiceScopeFactory _scopes;
    private readonly WebhookDispatcher _dispatcher;
    private readonly WebhookMetrics _webhookMetrics;
    private readonly IOptionsMonitor<WorkerOptions> _options;
    private readonly IOptionsMonitor<WebhookOptions> _webhooks;
    private readonly ILogger<WebhookDispatchWorker> _logger;

    /// <summary>
    /// Creates the worker.
    /// </summary>
    /// <param name="scopes">Scope factory.</param>
    /// <param name="dispatcher">The signer and sender.</param>
    /// <param name="webhookMetrics">Webhook instruments, for the dead letter gauge.</param>
    /// <param name="leader">Leader election.</param>
    /// <param name="metrics">Worker instruments.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="webhooks">Webhook options, which own the retry schedule.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WebhookDispatchWorker(
        IServiceScopeFactory scopes,
        WebhookDispatcher dispatcher,
        WebhookMetrics webhookMetrics,
        PostgresLeaderLock leader,
        WorkerMetrics metrics,
        IOptionsMonitor<WorkerOptions> options,
        IOptionsMonitor<WebhookOptions> webhooks,
        TimeProvider timeProvider,
        ILogger<WebhookDispatchWorker> logger)
        : base(leader, metrics, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(webhookMetrics);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(webhooks);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _dispatcher = dispatcher;
        _webhookMetrics = webhookMetrics;
        _options = options;
        _webhooks = webhooks;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override string JobName => Job;

    /// <inheritdoc />
    protected override bool IsEnabled =>
        _options.CurrentValue.Enabled && _options.CurrentValue.WebhookDispatch.Enabled;

    /// <inheritdoc />
    /// <remarks>
    /// Follows <c>Dle:Webhooks:PollIntervalSeconds</c> unless the worker section overrides it, so
    /// there are not two numbers that can disagree about how often the queue is drained.
    /// </remarks>
    protected override TimeSpan Interval
    {
        get
        {
            int configured = _options.CurrentValue.WebhookDispatch.IntervalMinutes;

            return configured > 0
                ? TimeSpan.FromMinutes(configured)
                : TimeSpan.FromSeconds(Math.Max(_webhooks.CurrentValue.PollIntervalSeconds, 1));
        }
    }

    /// <inheritdoc />
    protected override bool RunAtStartup => _options.CurrentValue.WebhookDispatch.RunAtStartup;

    /// <inheritdoc />
    protected override TimeSpan LockTimeout => TimeSpan.FromSeconds(_options.CurrentValue.LockTimeoutSeconds);

    /// <inheritdoc />
    protected override TimeSpan StartupJitter => TimeSpan.FromSeconds(_options.CurrentValue.StartupJitterSeconds);

    /// <inheritdoc />
    protected override async Task<string> RunPassAsync(CancellationToken cancellationToken)
    {
        WebhookOptions options = _webhooks.CurrentValue;

        using IServiceScope scope = _scopes.CreateScope();

        WebhookRepository repository = scope.ServiceProvider.GetRequiredService<WebhookRepository>();
        WebhookDeliveryStore store = scope.ServiceProvider.GetRequiredService<WebhookDeliveryStore>();

        IReadOnlyList<WebhookDelivery> claimed = await repository.ClaimDueAsync(options.BatchSize, cancellationToken);
        IReadOnlyList<PendingDelivery> pending = await store.ResolveAsync(claimed, cancellationToken);

        int delivered = 0;
        int failed = 0;
        int dead = 0;
        int orphaned = claimed.Count - pending.Count;

        foreach (PendingDelivery delivery in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!delivery.IsActive)
            {
                // The subscription was deactivated after the row was written. Retiring it as dead
                // is honest: it will never be delivered, and leaving it pending would keep the
                // queue growing behind a destination nobody is listening on.
                _ = await repository.MarkFailedAsync(
                    delivery.DeliveryId,
                    null,
                    "The subscription is no longer active.",
                    nextAttemptAt: null,
                    cancellationToken);

                dead++;
                _webhookMetrics.DeadLettered(delivery.EventType);
                continue;
            }

            WebhookAttempt attempt = await _dispatcher.SendAsync(
                delivery.Url,
                delivery.SecretEncrypted,
                delivery.EventType,
                delivery.Payload,
                cancellationToken);

            if (attempt.Succeeded)
            {
                _ = await repository.MarkDeliveredAsync(
                    delivery.DeliveryId,
                    attempt.StatusCode ?? 200,
                    cancellationToken);

                delivered++;
                continue;
            }

            bool exhausted = !attempt.Retryable || delivery.Attempt >= options.MaxAttempts;

            DateTimeOffset? nextAttemptAt = exhausted ? null : NextAttempt(options, delivery.Attempt);

            _ = await repository.MarkFailedAsync(
                delivery.DeliveryId,
                attempt.StatusCode,
                attempt.Error,
                nextAttemptAt,
                cancellationToken);

            if (exhausted)
            {
                dead++;
                _webhookMetrics.DeadLettered(delivery.EventType);
                LogDeadLettered(_logger, delivery.DeliveryId, delivery.EventType, delivery.Attempt);
            }
            else
            {
                failed++;
            }
        }

        if (orphaned > 0)
        {
            // The subscription row is gone entirely. Retiring these keeps the queue finite; the
            // count is logged because a non-zero value means somebody hard deleted a subscription,
            // which the API does not do.
            await RetireOrphansAsync(repository, claimed, pending, cancellationToken);
            LogOrphaned(_logger, orphaned);
        }

        long dlq = await store.CountDeadLetteredAsync(cancellationToken);
        _webhookMetrics.ReportDeadLetterQueueSize(dlq);

        Metrics.Items(Job, "delivery", claimed.Count);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{claimed.Count} claimed, {delivered} delivered, {failed} scheduled for retry, {dead} dead lettered; queue depth {dlq}.");
    }

    /// <summary>
    /// Computes when the next attempt is due: exponential backoff, spread by jitter.
    /// </summary>
    /// <param name="options">The webhook options, which own the schedule.</param>
    /// <param name="attempt">Attempts made so far, this one included.</param>
    /// <returns>The instant of the next attempt.</returns>
    /// <remarks>
    /// The jitter is drawn from the cryptographic generator, not because unpredictability is a
    /// security property here, but because it is the generator that is already available and needs
    /// no seeding discipline across replicas.
    /// </remarks>
    private DateTimeOffset NextAttempt(WebhookOptions options, int attempt)
    {
        TimeSpan delay = options.DelayFor(attempt);

        double spread = Math.Clamp(options.JitterRatio, 0d, 1d) * delay.TotalMilliseconds;

        if (spread >= 1d)
        {
            // Symmetric around the computed delay, so jitter neither systematically delays nor
            // systematically advances the schedule.
            double offset = RandomNumberGenerator.GetInt32((int)Math.Min(spread * 2d, int.MaxValue)) - spread;
            delay += TimeSpan.FromMilliseconds(offset);
        }

        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        return Clock.GetUtcNow() + delay;
    }

    /// <summary>Retires deliveries whose subscription no longer exists.</summary>
    private static async Task RetireOrphansAsync(
        WebhookRepository repository,
        IReadOnlyList<WebhookDelivery> claimed,
        IReadOnlyList<PendingDelivery> resolved,
        CancellationToken cancellationToken)
    {
        HashSet<Guid> known = [];

        foreach (PendingDelivery delivery in resolved)
        {
            _ = known.Add(delivery.DeliveryId);
        }

        foreach (WebhookDelivery delivery in claimed)
        {
            if (known.Contains(delivery.Id))
            {
                continue;
            }

            _ = await repository.MarkFailedAsync(
                delivery.Id,
                null,
                "The subscription this delivery belongs to no longer exists.",
                nextAttemptAt: null,
                cancellationToken);
        }
    }

    [LoggerMessage(
        EventId = 5640,
        Level = LogLevel.Warning,
        Message = "Webhook delivery {DeliveryId} of type {EventType} was dead lettered after {Attempt} attempts. "
            + "This is data the customer will not receive.")]
    private static partial void LogDeadLettered(ILogger logger, Guid deliveryId, string eventType, int attempt);

    [LoggerMessage(
        EventId = 5641,
        Level = LogLevel.Warning,
        Message = "{Count} claimed deliveries had no subscription and were retired.")]
    private static partial void LogOrphaned(ILogger logger, int count);
}
