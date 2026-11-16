using System.Security.Claims;
using System.Text.Json;

using Dle.Control.Features.Analytics;
using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Domain.Contracts;
using Dle.Domain.Entities;
using Dle.Domain.Ports;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// Covers C3: <c>DELETE /api/v1/privacy/installs/{installId}</c> — tenant-scoped DSAR erasure
/// and audit-log PII exclusion.
/// </summary>
public sealed class PrivacyErasureTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid ActorA = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task EraseInstall_DeletesOnlyCallerTenantRowsAndOmitsInstallIdFromAudit()
    {
        const string installId = "inst-user-secret-42";
        var store = new InMemoryErasureStore();
        store.Seed(TenantA, installId, installs: 1, attributions: 1, sdkEvents: 7);
        store.Seed(TenantB, installId, installs: 2, attributions: 2, sdkEvents: 5);

        var audit = new RecordingAuditWriter();
        DefaultHttpContext http = CreateHttpContext(TenantA);

        IResult result = await EraseInstallData.HandleAsync(
            "  " + installId + "  ",
            http,
            store,
            audit,
            CancellationToken.None);

        (int status, string json) = await ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status200OK, status);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("erased_installs").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("erased_attributions").GetInt32());
        Assert.Equal(7, doc.RootElement.GetProperty("erased_sdk_events").GetInt32());

        Assert.Equal(0, store.CountForTenant(TenantA, installId));
        Assert.Equal(2, store.CountForTenant(TenantB, installId));

        RecordingAuditWriter.AuditCall entry = Assert.Single(audit.Calls);
        Assert.Equal(AuditActions.PrivacyInstallErased, entry.Action);
        Assert.Equal(AuditActions.PrivacySubject, entry.SubjectType);
        Assert.Equal(TenantA.ToString(), entry.SubjectId);
        Assert.DoesNotContain(installId, entry.SubjectId, StringComparison.Ordinal);
        Assert.DoesNotContain(installId, entry.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("erased_sdk_events", entry.MetadataJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("bad\nnewline")]
    public async Task EraseInstall_InvalidInstallId_ReturnsValidationProblem(string installId)
    {
        var store = new InMemoryErasureStore();
        var audit = new RecordingAuditWriter();
        DefaultHttpContext http = CreateHttpContext(TenantA);

        IResult result = await EraseInstallData.HandleAsync(
            installId,
            http,
            store,
            audit,
            CancellationToken.None);

        (int status, string json) = await ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(ProblemCodes.ValidationFailed, doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, store.CallCount);
        Assert.Empty(audit.Calls);
    }

    [Fact]
    public async Task EraseInstall_OverMaxLength_ReturnsValidationProblem()
    {
        var store = new InMemoryErasureStore();
        var audit = new RecordingAuditWriter();
        DefaultHttpContext http = CreateHttpContext(TenantA);

        string oversized = new('a', 257);

        IResult result = await EraseInstallData.HandleAsync(
            oversized,
            http,
            store,
            audit,
            CancellationToken.None);

        (int status, string _) = await ExecuteResultAsync(result);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal(0, store.CallCount);
        Assert.Empty(audit.Calls);
    }

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

    private sealed class InMemoryErasureStore : IPrivacyErasureStore
    {
        private readonly Dictionary<(Guid TenantId, string InstallId), InstallErasureResult> _data = [];

        public int CallCount { get; private set; }

        public void Seed(Guid tenantId, string installId, int installs, int attributions, int sdkEvents) =>
            _data[(tenantId, installId)] = new InstallErasureResult(installs, attributions, sdkEvents);

        public int CountForTenant(Guid tenantId, string installId) =>
            _data.TryGetValue((tenantId, installId), out InstallErasureResult? r) ? r.ErasedInstalls : 0;

        public Task<InstallErasureResult> EraseInstallAsync(
            Guid tenantId,
            string installId,
            CancellationToken cancellationToken)
        {
            CallCount++;

            if (_data.Remove((tenantId, installId), out InstallErasureResult? removed))
            {
                return Task.FromResult(removed);
            }

            return Task.FromResult(new InstallErasureResult(0, 0, 0));
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
}
