using System.Collections.ObjectModel;

using Dle.Control.Features.Webhooks;

namespace Dle.ContractTests.Webhooks;

/// <summary>
/// The webhook payload contract of §B.7.4: one committed JSON Schema per event type, each checked
/// against a sample the product itself renders.
/// </summary>
/// <remarks>
/// <para>
/// A webhook is the only part of this system that runs inside somebody else's process. The receiver
/// is a customer's endpoint, written once against whatever a test delivery happened to contain, and
/// nobody redeploys it when this repository changes. That makes the envelope the most brittle
/// contract in the product and the one least likely to be noticed when it moves.
/// </para>
/// <para>
/// The sample is produced through <see cref="WebhookEventTypes.Render"/> — the same call the outbox
/// makes — rather than hand written, so the envelope's member names, its casing, the timestamp format
/// and the identifier format are the product's own output and not a transcription of it. The
/// <c>data</c> maps are transcribed from the emitters (LinkQuarantineService, ResolveInstall,
/// RecordEvents, DomainVerificationWorker, SendTestWebhook) and are the part a reviewer should check
/// when an emitter changes.
/// </para>
/// </remarks>
public sealed class WebhookPayloadContractTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid DeliveryId = new("0192f4f0-1c1a-7c3d-9b2e-4d5a6f7b8c9d");

    /// <summary>Every declared event type paired with the payload its emitter builds.</summary>
    public static TheoryData<string, Dictionary<string, string>> Samples()
    {
        TheoryData<string, Dictionary<string, string>> data = new();

        // ResolveInstall.AnnounceAsync / RecordEvents.AnnounceAsync.
        data.Add(WebhookEventTypes.AttributionCreated, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["install_id"] = "9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1",
            ["app_id"] = "3b81d47e-6c25-4f19-8a30-5d7c9e2b4f16",
            ["match_type"] = "install_referrer",
            ["confidence"] = "1.00",
            ["click_id"] = "aB3xK9pQ",
            ["link_id"] = "7286414500000000001",
        });

        // Declared in WebhookEventTypes.All and therefore subscribable.
        data.Add(WebhookEventTypes.LinkCreated, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["link_id"] = "7286414500000000001",
        });

        data.Add(WebhookEventTypes.LinkUpdated, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["link_id"] = "7286414500000000001",
        });

        // LinkQuarantineService.AnnounceAsync.
        data.Add(WebhookEventTypes.LinkQuarantined, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["link_id"] = "7286414500000000001",
            ["reason"] = "phishing",
            ["decided_at"] = Moment.ToString("O", CultureInfo.InvariantCulture),
        });

        data.Add(WebhookEventTypes.LinkReleased, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["link_id"] = "7286414500000000001",
            ["reason"] = "appeal upheld",
            ["decided_at"] = Moment.ToString("O", CultureInfo.InvariantCulture),
        });

        // DomainVerificationWorker.AnnounceAsync.
        data.Add(WebhookEventTypes.DomainVerificationFailed, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["domain_id"] = "5d7c9e2b-4f16-4a8e-9c2d-8f4c1f0a77b1",
            ["host"] = "link.example.com",
            ["takeover_risk"] = "false",
            ["aasa"] = "failed",
            ["aasa_codes"] = "redirected,not_json",
        });

        // SendTestWebhook.
        data.Add(WebhookEventTypes.Test, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["subscription_id"] = "8a30d47e-6c25-4f19-8a30-5d7c9e2b4f16",
            ["message"] = "This is a test delivery produced by the /test endpoint. No event occurred.",
        });

        return data;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    [Trait("Contract", "B.7.4")]
    public void RenderedPayload_SatisfiesTheCommittedSchemaForItsEventType(
        string eventType,
        Dictionary<string, string> data)
    {
        string payload = WebhookEventTypes.Render(
            eventType,
            DeliveryId,
            Moment,
            new ReadOnlyDictionary<string, string>(data));

        JsonSchemaValidator schema = SchemaFor(eventType);

        IReadOnlyList<SchemaViolation> violations = schema.ValidateJson(payload);

        Assert.True(
            violations.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 The rendered {eventType} payload does not satisfy its committed schema.

                 {payload}

                 {JsonSchemaValidator.Describe(violations)}
                 """));
    }

    [Fact]
    [Trait("Contract", "B.7.4")]
    public void EveryDeclaredEventType_HasACommittedSchema()
    {
        // The regression this guards is adding an event type without publishing what it carries: a
        // customer can subscribe to it, and then has to reverse engineer the payload from a live
        // delivery.
        List<string> missing =
            [.. WebhookEventTypes.All.Where(type => !File.Exists(SchemaPath(type)))];

        Assert.True(
            missing.Count == 0,
            "These event types can be subscribed to but publish no payload schema: "
                + string.Join(", ", missing));
    }

    [Fact]
    [Trait("Contract", "B.7.4")]
    public void EveryCommittedSchema_BelongsToADeclaredEventType()
    {
        // The other direction. A schema for an event type that no longer exists is documentation for
        // something a customer can never receive.
        List<string> orphaned =
            [.. Directory.EnumerateFiles(RepositoryLayout.ContractsDirectory, "webhook.*.schema.json")
                .Select(Path.GetFileName)
                .Select(name => name!["webhook.".Length..^".schema.json".Length])
                .Where(type => !WebhookEventTypes.IsKnown(type))];

        Assert.True(
            orphaned.Count == 0,
            "These schemas describe event types the product does not declare: " + string.Join(", ", orphaned));
    }

    [Fact]
    [Trait("Contract", "B.7.4")]
    public void Envelope_HasExactlyTheFourMembersOfB74()
    {
        string payload = WebhookEventTypes.Render(
            WebhookEventTypes.Test,
            DeliveryId,
            Moment,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" });

        const string expected =
            """
            {"event":"webhook.test","id":"0192f4f0-1c1a-7c3d-9b2e-4d5a6f7b8c9d","occurred_at":"2026-09-03T10:00:00+00:00","data":{"k":"v"}}
            """;

        // Literal, because §B.7.4 prints the envelope literally and because a receiver written in
        // three languages parses these four names.
        Assert.Equal(expected, payload);
    }

    [Fact]
    [Trait("Contract", "B.7.4")]
    public void Envelope_PreservesDataKeysVerbatim()
    {
        // The naming policy renames properties, not dictionary keys. A `data` map whose keys were
        // rewritten would silently rename fields the receiver branches on.
        string payload = WebhookEventTypes.Render(
            WebhookEventTypes.Test,
            DeliveryId,
            Moment,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["campaignId"] = "jesen26" });

        Assert.Contains("\"campaignId\":\"jesen26\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Contract", "B.7.4")]
    public void AttributionEventName_IsSpelledTheSameInBothPlacesItIsDeclared()
    {
        // The string exists twice in the product: WebhookEventTypes.AttributionCreated, which the
        // subscription validator checks against, and WebhookEvents.AttributionCreated, which the
        // attribution slice emits with. If the two ever diverge, subscriptions validate against one
        // spelling and deliveries carry the other, and nothing in the compiler notices.
        Assert.Equal(
            WebhookEventTypes.AttributionCreated,
            Dle.Control.Features.Attribution.WebhookEvents.AttributionCreated);
    }

    private static string SchemaPath(string eventType) =>
        Path.Combine(RepositoryLayout.ContractsDirectory, "webhook." + eventType + ".schema.json");

    private static JsonSchemaValidator SchemaFor(string eventType) =>
        JsonSchemaValidator.Parse(RepositoryLayout.ReadContract("webhook." + eventType + ".schema.json"));
}
