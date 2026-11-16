using System.Security.Claims;
using System.Text.Json;

using Dle.Control.Features.Shared;
using Dle.Control.Features.Webhooks;
using Dle.Control.Identity;
using Dle.Domain.Contracts;
using Dle.Domain.Entities;
using Dle.Domain.Ports;
using Dle.Persistence.Repositories;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// Covers <c>POST /api/v1/webhooks/deliveries/{id}/replay</c> and
/// <c>POST /api/v1/webhooks/deliveries/replay-dead</c>.
/// </summary>
public sealed class WebhookReplayTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid ActorA = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task SingleReplay_DeadDelivery_RequeuesAndWritesAudit()
    {
        var subId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var store = new InMemoryWebhookReplayStore(TenantA);
        store.Subscriptions.Add((subId, TenantA, true));
        store.Deliveries.Add(new WebhookDelivery
        {
            Id = deliveryId,
            TenantId = TenantA,
            SubscriptionId = subId,
            EventType = "attribution.created",
            Status = WebhookRepository.DeadStatus,
            Attempt = 5,
            LastError = "timeout",
            ResponseCode = 504,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        });

        var audit = new RecordingAuditWriter();
        DefaultHttpContext http = CreateHttpContext(TenantA);

        IResult result = await ReplayWebhookDelivery.HandleSingleAsync(
            deliveryId,
            http,
            store,
            audit,
            CancellationToken.None);

        (int status, string json) = await ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status200OK, status);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("replayed_count").GetInt32());
        Assert.Equal(deliveryId, doc.RootElement.GetProperty("delivery_ids")[0].GetGuid());

        WebhookDelivery updated = Assert.Single(store.Deliveries);
        Assert.Equal(WebhookRepository.PendingStatus, updated.Status);
        Assert.Equal(0, updated.Attempt);
        Assert.Null(updated.LastError);
        Assert.Null(updated.ResponseCode);
        Assert.NotNull(updated.NextAttemptAt);

        RecordingAuditWriter.AuditCall recorded = Assert.Single(audit.Calls);
        Assert.Equal(AuditActions.WebhookDeliveryReplayed, recorded.Action);
        Assert.Equal(AuditActions.WebhookSubject, recorded.SubjectType);
        Assert.Equal(deliveryId.ToString(), recorded.SubjectId);
    }

    [Fact]
    public async Task SingleReplay_Delivered_ReturnsConflictWithoutAudit()
    {
        var subId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var store = new InMemoryWebhookReplayStore(TenantA);
        store.Subscriptions.Add((subId, TenantA, true));
        store.Deliveries.Add(new WebhookDelivery
        {
            Id = deliveryId,
            TenantId = TenantA,
            SubscriptionId = subId,
            EventType = "link.created",
            Status = WebhookRepository.DeliveredStatus,
            Attempt = 1,
            ResponseCode = 200,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var audit = new RecordingAuditWriter();
        DefaultHttpContext http = CreateHttpContext(TenantA);

        IResult result = await ReplayWebhookDelivery.HandleSingleAsync(
            deliveryId,
            http,
            store,
            audit,
            CancellationToken.None);

        (int status, string json) = await ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status409Conflict, status);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(ProblemCodes.WriteConflict, doc.RootElement.GetProperty("type").GetString());
        Assert.Empty(audit.Calls);
    }

    [Fact]
    public async Task SingleReplay_InactiveSubscription_ReturnsConflict()
    {
        var subId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var store = new InMemoryWebhookReplayStore(TenantA);
        store.Subscriptions.Add((subId, TenantA, false));
        store.Deliveries.Add(new WebhookDelivery
        {
            Id = deliveryId,
            TenantId = TenantA,
            SubscriptionId = subId,
            EventType = "link.created",
            Status = WebhookRepository.DeadStatus,
            Attempt = 4,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var audit = new RecordingAuditWriter();
        DefaultHttpContext http = CreateHttpContext(TenantA);

        IResult result = await ReplayWebhookDelivery.HandleSingleAsync(
            deliveryId,
            http,
            store,
            audit,
            CancellationToken.None);

        (int status, string _) = await ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Empty(audit.Calls);
    }

    [Fact]
    public async Task SingleReplay_OtherTenantDelivery_ReturnsNotFound()
    {
        var subId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var store = new InMemoryWebhookReplayStore(TenantA);
        store.Subscriptions.Add((subId, TenantB, true));
        store.Deliveries.Add(new WebhookDelivery
        {
            Id = deliveryId,
            TenantId = TenantB,
            SubscriptionId = subId,
            EventType = "link.created",
            Status = WebhookRepository.DeadStatus,
            Attempt = 4,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var audit = new RecordingAuditWriter();
        DefaultHttpContext http = CreateHttpContext(TenantA);

        IResult result = await ReplayWebhookDelivery.HandleSingleAsync(
            deliveryId,
            http,
            store,
            audit,
            CancellationToken.None);

        (int status, string _) = await ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Empty(audit.Calls);
    }

    [Fact]
    public async Task ReplayDead_RespectsSubscriptionFilterLimitAndActiveStatus()
    {
        var subActive = Guid.NewGuid();
        var subOther = Guid.NewGuid();
        var subInactive = Guid.NewGuid();
        var store = new InMemoryWebhookReplayStore(TenantA);
        store.Subscriptions.Add((subActive, TenantA, true));
        store.Subscriptions.Add((subOther, TenantA, true));
        store.Subscriptions.Add((subInactive, TenantA, false));

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var idInactive = Guid.NewGuid();
        var idOtherTenant = Guid.NewGuid();

        DateTimeOffset t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        store.Deliveries.Add(Dead(id1, TenantA, subActive, t0.AddMinutes(3)));
        store.Deliveries.Add(Dead(id2, TenantA, subActive, t0.AddMinutes(2)));
        store.Deliveries.Add(Dead(id3, TenantA, subOther, t0.AddMinutes(4)));
        store.Deliveries.Add(Dead(idInactive, TenantA, subInactive, t0.AddMinutes(5)));
        store.Deliveries.Add(Dead(idOtherTenant, TenantB, subActive, t0.AddMinutes(6)));

        var audit = new RecordingAuditWriter();
        DefaultHttpContext http = CreateHttpContext(TenantA);
        IOptionsMonitor<WebhookOptions> options = new StaticOptionsMonitor<WebhookOptions>(new WebhookOptions
        {
            MaxDeliveryPageSize = 10,
        });

        IResult result = await ReplayWebhookDelivery.HandleDeadAsync(
            subActive,
            limit: 1,
            http,
            store,
            options,
            audit,
            CancellationToken.None);

        (int status, string json) = await ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status200OK, status);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("replayed_count").GetInt32());
        Assert.Equal(id1, doc.RootElement.GetProperty("delivery_ids")[0].GetGuid());
        Assert.Single(audit.Calls);
    }

    [Fact]
    public async Task ReplayDead_ZeroDeadDeliveries_ReturnsZeroWithoutWritingAudit()
    {
        var store = new InMemoryWebhookReplayStore(TenantA);
        var audit = new RecordingAuditWriter();
        DefaultHttpContext http = CreateHttpContext(TenantA);
        IOptionsMonitor<WebhookOptions> options = new StaticOptionsMonitor<WebhookOptions>(new WebhookOptions());

        IResult result = await ReplayWebhookDelivery.HandleDeadAsync(
            subscriptionId: null,
            limit: null,
            http,
            store,
            options,
            audit,
            CancellationToken.None);

        (int status, string json) = await ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status200OK, status);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("replayed_count").GetInt32());
        Assert.Empty(audit.Calls);
    }

    private static WebhookDelivery Dead(Guid id, Guid tenantId, Guid subId, DateTimeOffset createdAt) => new()
    {
        Id = id,
        TenantId = tenantId,
        SubscriptionId = subId,
        EventType = "attribution.created",
        Status = WebhookRepository.DeadStatus,
        Attempt = 5,
        CreatedAt = createdAt,
    };

    private static DefaultHttpContext CreateHttpContext(Guid tenantId)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(DleClaimTypes.TenantId, tenantId.ToString()),
            new Claim(DleClaimTypes.ActorId, ActorA.ToString()),
            new Claim(DleClaimTypes.ActorType, DleCaller.ApiKeyActor),
            new Claim(DleClaimTypes.Role, DleRoles.Owner),
        ],
        authenticationType: "Test");

        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };

        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        return (http.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class InMemoryWebhookReplayStore(Guid callerTenantId) : IWebhookReplayStore
    {
        public List<(Guid Id, Guid TenantId, bool IsActive)> Subscriptions { get; } = [];
        public List<WebhookDelivery> Deliveries { get; } = [];

        public Task<WebhookSingleReplayResult> ReplayDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken)
        {
            WebhookDelivery? delivery = Deliveries.FirstOrDefault(
                d => d.Id == deliveryId && d.TenantId == callerTenantId);

            if (delivery is null)
            {
                return Task.FromResult(new WebhookSingleReplayResult(WebhookReplayStatus.NotFound));
            }

            bool active = Subscriptions.Any(
                s => s.Id == delivery.SubscriptionId && s.TenantId == callerTenantId && s.IsActive);

            if (!active)
            {
                return Task.FromResult(new WebhookSingleReplayResult(WebhookReplayStatus.SubscriptionInactive));
            }

            if (string.Equals(delivery.Status, WebhookRepository.DeliveredStatus, StringComparison.Ordinal))
            {
                return Task.FromResult(new WebhookSingleReplayResult(WebhookReplayStatus.AlreadyDelivered));
            }

            delivery.Status = WebhookRepository.PendingStatus;
            delivery.Attempt = 0;
            delivery.NextAttemptAt = DateTimeOffset.UtcNow;
            delivery.LastError = null;
            delivery.ResponseCode = null;

            return Task.FromResult(new WebhookSingleReplayResult(WebhookReplayStatus.Replayed, delivery.Id));
        }

        public Task<WebhookBatchReplayResult> ReplayDeadDeliveriesAsync(
            Guid? subscriptionId,
            int limit,
            CancellationToken cancellationToken)
        {
            HashSet<Guid> activeSubs = Subscriptions
                .Where(s => s.TenantId == callerTenantId && s.IsActive && (!subscriptionId.HasValue || s.Id == subscriptionId.Value))
                .Select(s => s.Id)
                .ToHashSet();

            List<WebhookDelivery> matches = Deliveries
                .Where(d => d.TenantId == callerTenantId
                    && d.Status == WebhookRepository.DeadStatus
                    && activeSubs.Contains(d.SubscriptionId))
                .OrderByDescending(d => d.CreatedAt)
                .Take(Math.Clamp(limit, 1, 500))
                .ToList();

            foreach (WebhookDelivery delivery in matches)
            {
                delivery.Status = WebhookRepository.PendingStatus;
                delivery.Attempt = 0;
                delivery.NextAttemptAt = DateTimeOffset.UtcNow;
                delivery.LastError = null;
                delivery.ResponseCode = null;
            }

            return Task.FromResult(new WebhookBatchReplayResult([.. matches.Select(m => m.Id)]));
        }
    }

    private sealed class RecordingAuditWriter : IAuditLogWriter
    {
        public sealed record AuditCall(string Action, string SubjectType, string SubjectId, string? MetadataJson);

        public List<AuditCall> Calls { get; } = [];

        public Task<AuditLogEntry> WriteAsync(
            string action,
            string subjectType,
            string subjectId,
            string actorType,
            Guid? actorId,
            string? metadataJson,
            CancellationToken cancellationToken)
        {
            Calls.Add(new AuditCall(action, subjectType, subjectId, metadataJson));
            return Task.FromResult(new AuditLogEntry
            {
                Action = action,
                SubjectType = subjectType,
                SubjectId = subjectId,
                ActorType = actorType,
                ActorId = actorId,
                Metadata = metadataJson ?? "{}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
