using Dle.Domain.Entities;

using Microsoft.EntityFrameworkCore;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// One delivery joined to the destination it belongs to.
/// </summary>
/// <param name="DeliveryId">The delivery row.</param>
/// <param name="TenantId">Tenant the delivery belongs to.</param>
/// <param name="SubscriptionId">Subscription the delivery belongs to.</param>
/// <param name="EventType">Event type being delivered.</param>
/// <param name="Payload">The exact bytes to send, as rendered when the event was recorded.</param>
/// <param name="Attempt">Attempts made so far, this one included.</param>
/// <param name="Url">Destination URL.</param>
/// <param name="SecretEncrypted">The subscription's shared secret, as stored.</param>
/// <param name="IsActive">Whether the subscription still wants deliveries.</param>
public sealed record PendingDelivery(
    Guid DeliveryId,
    Guid TenantId,
    Guid SubscriptionId,
    string EventType,
    string Payload,
    int Attempt,
    string Url,
    byte[] SecretEncrypted,
    bool IsActive);

/// <summary>
/// The reads the delivery worker needs that cross tenants (§B.3 component C-10).
/// </summary>
/// <remarks>
/// <para>
/// <c>WebhookRepository</c> claims and completes deliveries; what it deliberately does not do is
/// hand out a subscription's secret, because nothing in the control plane API ever should. The
/// dispatcher does need it, once per attempt, so the join lives here — in the module that signs —
/// rather than in the repository every request handler can reach.
/// </para>
/// <para>
/// Every query opens an explicit cross tenant scope with a written reason. The worker drains one
/// queue for the whole instance, so it is one of the few places where the tenant filter genuinely
/// has to be stepped around; making that explicit and greppable is the point of the mechanism.
/// </para>
/// </remarks>
public sealed class WebhookDeliveryStore
{
    private readonly DleDbContext _db;

    /// <summary>Creates the store.</summary>
    /// <param name="db">The control plane context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> is <see langword="null"/>.</exception>
    public WebhookDeliveryStore(DleDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
    }

    /// <summary>
    /// Joins claimed deliveries to their destinations.
    /// </summary>
    /// <param name="deliveries">The deliveries a worker has just claimed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One entry per delivery whose subscription still exists. A delivery whose subscription was
    /// deleted is absent, and the caller retires it rather than retrying it forever.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="deliveries"/> is <see langword="null"/>.</exception>
    public async Task<IReadOnlyList<PendingDelivery>> ResolveAsync(
        IReadOnlyList<WebhookDelivery> deliveries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deliveries);

        if (deliveries.Count == 0)
        {
            return [];
        }

        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "the delivery worker drains the queue of every tenant");

        List<Guid> subscriptionIds = [];

        foreach (WebhookDelivery delivery in deliveries)
        {
            if (!subscriptionIds.Contains(delivery.SubscriptionId))
            {
                subscriptionIds.Add(delivery.SubscriptionId);
            }
        }

        Dictionary<Guid, WebhookSubscription> subscriptions = await _db.WebhookSubscriptions
            .AsNoTracking()
            .AcrossTenants()
            .Where(s => subscriptionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        List<PendingDelivery> resolved = new(deliveries.Count);

        foreach (WebhookDelivery delivery in deliveries)
        {
            if (!subscriptions.TryGetValue(delivery.SubscriptionId, out WebhookSubscription? subscription))
            {
                continue;
            }

            resolved.Add(new PendingDelivery(
                delivery.Id,
                delivery.TenantId,
                delivery.SubscriptionId,
                delivery.EventType,
                delivery.Payload,
                delivery.Attempt,
                subscription.Url,
                subscription.SecretEncrypted,
                subscription.IsActive));
        }

        return resolved;
    }

    /// <summary>
    /// Counts the deliveries currently in the dead letter queue, across every tenant.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The depth of the queue, for <c>dle_webhook_dlq_size</c> (§C.6).</returns>
    public async Task<long> CountDeadLetteredAsync(CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "the dead letter gauge is instance wide, not tenant scoped");

        return await _db.WebhookDeliveries
            .AsNoTracking()
            .AcrossTenants()
            .LongCountAsync(d => d.Status == WebhookRepository.DeadStatus, cancellationToken);
    }

    /// <summary>
    /// Reads one subscription of the tenant in scope, for the test delivery endpoint.
    /// </summary>
    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscription, or <see langword="null"/> when it does not exist <em>in this
    /// tenant</em> — the two are indistinguishable on purpose (SHARED-KERNEL §17.7, TC-166).</returns>
    public async Task<WebhookSubscription?> FindAsync(Guid subscriptionId, CancellationToken cancellationToken) =>
        await _db.WebhookSubscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);

    /// <summary>Counts the subscriptions of the tenant in scope, for the quota check.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of subscriptions the tenant already has.</returns>
    public async Task<int> CountAsync(CancellationToken cancellationToken) =>
        await _db.WebhookSubscriptions.AsNoTracking().CountAsync(cancellationToken);
}
