using Dle.Domain.Ports;

using Microsoft.Extensions.Options;

namespace Dle.Persistence.Repositories;

/// <summary>
/// Reads and writes webhook subscriptions and their delivery queue (FR-204), and serves as the
/// transactional outbox behind <see cref="IWebhookOutbox"/>.
/// </summary>
/// <remarks>
/// The outbox is a table rather than a queue for one reason: a postback must not be promised for an
/// event whose transaction later rolls back. Writing the delivery row in the same transaction as
/// the change that caused it makes the two atomic, and the worker picks it up afterwards.
/// </remarks>
public sealed class WebhookRepository : IWebhookOutbox
{
    /// <summary>Delivery state of a row waiting to be sent.</summary>
    public const string PendingStatus = "pending";

    /// <summary>Delivery state of a row that was accepted by the endpoint.</summary>
    public const string DeliveredStatus = "delivered";

    /// <summary>Delivery state of a row whose last attempt failed and will be retried.</summary>
    public const string FailedStatus = "failed";

    /// <summary>Delivery state of a row that exhausted its retries.</summary>
    public const string DeadStatus = "dead";

    private readonly DleDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<DlePersistenceOptions> _options;

    /// <summary>Creates the repository.</summary>
    /// <param name="db">The control-plane context.</param>
    /// <param name="timeProvider">Clock used for scheduling and leases.</param>
    /// <param name="options">Persistence options, for the claim lease.</param>
    public WebhookRepository(
        DleDbContext db,
        TimeProvider timeProvider,
        IOptions<DlePersistenceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);

        _db = db;
        _timeProvider = timeProvider;
        _options = options;
    }

    /// <summary>Lists the subscriptions of the tenant in scope.</summary>
    /// <param name="onlyActive">Whether inactive subscriptions are hidden.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscriptions, newest first.</returns>
    public async Task<IReadOnlyList<WebhookSubscription>> ListSubscriptionsAsync(
        bool onlyActive,
        CancellationToken cancellationToken)
    {
        IQueryable<WebhookSubscription> query = _db.WebhookSubscriptions.AsNoTracking();

        if (onlyActive)
        {
            query = query.Where(s => s.IsActive);
        }

        return await query
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Registers a subscription for the tenant in scope.</summary>
    /// <param name="subscription">The subscription.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored subscription.</returns>
    public async Task<WebhookSubscription> AddSubscriptionAsync(
        WebhookSubscription subscription,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        _db.WebhookSubscriptions.Add(subscription);
        await _db.SaveChangesAsync(cancellationToken);
        return subscription;
    }

    /// <summary>Stops delivering to a subscription of the tenant in scope.</summary>
    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a row was updated.</returns>
    public async Task<bool> DeactivateSubscriptionAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        WebhookSubscription? subscription = await _db.WebhookSubscriptions
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);

        if (subscription is null)
        {
            return false;
        }

        subscription.IsActive = false;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One delivery row per matching subscription. The payload is stored as given, because the
    /// signature covers these exact bytes and a retry has to send them again unchanged.
    /// </remarks>
    public async Task EnqueueAsync(
        Guid tenantId,
        string eventType,
        string payloadJson,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        // The port takes the tenant as an argument because an event can be produced by a worker
        // iterating tenants, so the ambient tenant is not necessarily the subject of the event.
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "the outbox is written for the tenant named by the event, not by the ambient scope");

        List<Guid> subscriptionIds = await _db.WebhookSubscriptions
            .AsNoTracking()
            .AcrossTenants()
            .Where(s => s.TenantId == tenantId && s.IsActive && s.EventTypes.Contains(eventType))
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (subscriptionIds.Count == 0)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        foreach (Guid subscriptionId in subscriptionIds)
        {
            _db.WebhookDeliveries.Add(new WebhookDelivery
            {
                TenantId = tenantId,
                SubscriptionId = subscriptionId,
                EventType = eventType,
                Payload = payloadJson,
                Attempt = 0,
                Status = PendingStatus,
                NextAttemptAt = now,
                CreatedAt = now,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Claims a batch of deliveries that are due, so that no other worker takes the same rows.
    /// </summary>
    /// <param name="batchSize">Maximum number of deliveries to claim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The claimed deliveries, with the attempt counter already incremented.</returns>
    /// <remarks>
    /// <para>
    /// The claim is a compare and swap on <c>(id, attempt)</c>: the row is read, then updated only
    /// if the attempt counter is still what was read. Exactly one worker wins each row; the losers
    /// see zero rows affected and move on. The winner pushes <c>next_attempt_at</c> a lease into the
    /// future, so a worker that dies mid-flight releases its rows by expiry rather than by holding
    /// them forever.
    /// </para>
    /// <para>
    /// <c>SELECT … FOR UPDATE SKIP LOCKED</c> would express the same thing in one statement, but EF
    /// Core composes a raw SQL query into a subquery in order to apply the query filters, and
    /// <c>UPDATE … RETURNING</c> is not valid in that position. Dropping the filters to get around
    /// it is exactly the trade this project refuses to make, and the delivery worker handles tens of
    /// rows a second, not thousands.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<WebhookDelivery>> ClaimDueAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "the delivery worker drains the queue of every tenant");

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset leaseUntil = now.AddSeconds(_options.Value.WebhookLeaseSeconds);

        List<WebhookDelivery> candidates = await _db.WebhookDeliveries
            .AsNoTracking()
            .AcrossTenants()
            .Where(d => d.Status == PendingStatus && d.NextAttemptAt != null && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        List<WebhookDelivery> claimed = new(candidates.Count);

        foreach (WebhookDelivery candidate in candidates)
        {
            int observedAttempt = candidate.Attempt;

            int affected = await _db.WebhookDeliveries
                .AcrossTenants()
                .Where(d => d.Id == candidate.Id
                    && d.Attempt == observedAttempt
                    && d.Status == PendingStatus)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(d => d.Attempt, observedAttempt + 1)
                        .SetProperty(d => d.NextAttemptAt, leaseUntil),
                    cancellationToken);

            if (affected == 1)
            {
                candidate.Attempt = observedAttempt + 1;
                candidate.NextAttemptAt = leaseUntil;
                claimed.Add(candidate);
            }
        }

        return claimed;
    }

    /// <summary>Records a successful delivery.</summary>
    /// <param name="deliveryId">The delivery.</param>
    /// <param name="responseCode">HTTP status returned by the endpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    public async Task<int> MarkDeliveredAsync(
        Guid deliveryId,
        int responseCode,
        CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "the delivery worker drains the queue of every tenant");

        DateTimeOffset now = _timeProvider.GetUtcNow();

        return await _db.WebhookDeliveries
            .AcrossTenants()
            .Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(d => d.Status, DeliveredStatus)
                    .SetProperty(d => d.ResponseCode, responseCode)
                    .SetProperty(d => d.DeliveredAt, now)
                    .SetProperty(d => d.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(d => d.LastError, (string?)null),
                cancellationToken);
    }

    /// <summary>Records a failed attempt and schedules the next one, or gives up.</summary>
    /// <param name="deliveryId">The delivery.</param>
    /// <param name="responseCode">HTTP status returned, when there was one.</param>
    /// <param name="error">Short description of the failure. Never the request headers, which carry
    /// the signature and any customer credentials.</param>
    /// <param name="nextAttemptAt">When to try again, or <see langword="null"/> when giving up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    public async Task<int> MarkFailedAsync(
        Guid deliveryId,
        int? responseCode,
        string? error,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "the delivery worker drains the queue of every tenant");

        string status = nextAttemptAt is null ? DeadStatus : PendingStatus;

        return await _db.WebhookDeliveries
            .AcrossTenants()
            .Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(d => d.Status, status)
                    .SetProperty(d => d.ResponseCode, responseCode)
                    .SetProperty(d => d.LastError, error)
                    .SetProperty(d => d.NextAttemptAt, nextAttemptAt),
                cancellationToken);
    }

    /// <summary>Reads the recent deliveries of the tenant in scope, for the customer's own view.</summary>
    /// <param name="subscriptionId">Restrict to one subscription, or <see langword="null"/>.</param>
    /// <param name="limit">Maximum number of rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deliveries, newest first.</returns>
    public async Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(
        Guid? subscriptionId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        IQueryable<WebhookDelivery> query = _db.WebhookDeliveries.AsNoTracking();

        if (subscriptionId is Guid id)
        {
            query = query.Where(d => d.SubscriptionId == id);
        }

        return await query
            .OrderByDescending(d => d.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
