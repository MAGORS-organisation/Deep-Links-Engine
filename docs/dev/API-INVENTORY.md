# Dle.Domain — public API inventory

Generated from source. Authoritative for what exists; `docs/dev/SHARED-KERNEL.md` is authoritative for semantics.

## Dle.Domain.Abuse
```
public enum AbuseReason
public enum AbuseReportStatus
public enum UrlSafetyLevel
public sealed record UrlSafetyVerdict
public static class TargetUrlPolicy
```

## Dle.Domain.Analytics
```
public enum SdkEventType
public enum TimeGrain
public sealed record AnalyticsQuery
public sealed record BreakdownRow(string Key, long Clicks, long Installs, decimal? Value);
public sealed record ClickEvent
public sealed record FunnelSummary(
public sealed record MatchTypeSummary(string MatchType, long Count, decimal AverageConfidence);
public sealed record SdkEvent
public sealed record TimeSeriesPoint(DateTimeOffset Bucket, long Clicks, long Installs, long Conversions);
public static class DecisionNames
```

## Dle.Domain.Attribution
```
public enum MatchType
public sealed record AttributionResult
public sealed record DeviceSignals
public sealed record ResolveRequest
public static class ClaimCode
public static class InstallReferrerParser
public static class MatchTypeNames
public static class ProbabilisticScorer
```

## Dle.Domain.Clients
```
public enum ClientChannel
public enum DeviceClass
public enum Platform
public sealed record ClientContext
public sealed record ClientRequest
public sealed record GeoLocation
```

## Dle.Domain.Contracts
```
public sealed record AbuseReportResponse
public sealed record AnalyticsQueryRequest
public sealed record ApiKeyCreatedResponse
public sealed record ApiKeyResponse
public sealed record AppResponse
public sealed record AttributionQualityResponse
public sealed record BreakdownResponse
public sealed record BulkLinkItem
public sealed record BulkLinkResult
public sealed record ConsentDto
public sealed record CreateAbuseReportRequest
public sealed record CreateApiKeyRequest
public sealed record CreateAppRequest
public sealed record CreateDomainRequest
public sealed record CreateLinkRequest
public sealed record CreateTenantRequest
public sealed record CreateWebhookRequest
public sealed record DeviceSignalsDto
public sealed record DomainResponse
public sealed record DomainVerificationResponse
public sealed record EventBatchAcceptedDto
public sealed record EventBatchDto
public sealed record EventDto
public sealed record LinkResponse
public sealed record LinkVersionResponse
public sealed record PagedResponse<T>
public sealed record ResolveLinkDto
public sealed record ResolveRequestDto
public sealed record ResolveResponseDto
public sealed record SimulateRequest
public sealed record SimulateResponse
public sealed record TenantResponse
public sealed record TimeSeriesResponse
public sealed record UpdateLinkRequest
public sealed record VerificationCheckResult
public sealed record WebhookPayload
public sealed record WebhookResponse
public static class ProblemCodes
```

## Dle.Domain.Crypto
```
public interface IClickIdCodec
public interface IFeistelPermutation
public interface IIpHasher
public interface IKeyRing
public interface ISigner
public interface ISlugGenerator
public interface IVerifier
public sealed record JsonWebKey
public sealed record JwksDocument(IReadOnlyList<JsonWebKey> Keys);
public sealed record SignedToken(string Alg, string Kid, string Payload, string Signature)
public static class SignatureAlgorithms
```

## Dle.Domain.Entities
```
public class AbuseReport
public class ApiKey
public class App
public class AppDomain
public class AttributionRecord
public class AuditLogEntry
public class Campaign
public class ClaimCodeRecord
public class DomainVerification
public class IdempotencyRecord
public class Install
public class Link
public class LinkDomain
public class LinkVersion
public class SdkKey
public class SigningKeyRecord
public class SlugSequence
public class Tenant
public class WebhookDelivery
public class WebhookSubscription
```

## Dle.Domain.GlobalUsings.cs
```
```

## Dle.Domain.Links
```
public enum LinkServeState
public sealed record LinkSnapshot
public sealed record OgMeta
```

## Dle.Domain.Ports
```
public interface IBotVerifier
public interface IClickAnalyticsStore
public interface IClickEventSink
public interface IClickEventWriter
public interface IClickLookup
public interface IClientClassifier
public interface IDomainConfigStore
public interface IGeoIpResolver
public interface ILinkCacheInvalidator
public interface ILinkStore
public interface ISdkEventWriter
public interface IUrlSafetyChecker
public interface IWebhookOutbox
public sealed record ClickRecord
public sealed record DomainRuntimeConfig
```

## Dle.Domain.Primitives
```
public static class Base62
public static class HostNormalizer
public static class SlugPolicy
public static class VersionComparer
```

## Dle.Domain.Privacy
```
public enum ConsentMode
public sealed record ConsentDecision
public sealed record ConsentSignal
public static class ConsentGate
```

## Dle.Domain.Routing
```
public enum DecisionKind
public enum InterstitialMode
public enum RoutingActionKind
public interface IRoutingEngine
public sealed class RoutingEngine : IRoutingEngine
public sealed record AbVariant
public sealed record RoutingDecision
public sealed record RoutingRule
public sealed record RoutingValidationError(string Path, string Message);
public sealed record RuleAction
public sealed record RuleCondition
public sealed record TimeWindowPredicate
public sealed record VersionPredicate
public static class ChannelNames
public static class ConsistentBucket
public static class RoutingRuleValidator
public static class RoutingUrlBuilder
```

## Dle.Domain.Serialization
```
public sealed partial class DleDomainJsonContext : JsonSerializerContext;
public static class DleJson
```

## Dle.Domain.WellKnown
```
public sealed record AasaAppEntry
public sealed record AasaComponent
public sealed record AndroidAppEntry
public sealed record WellKnownDocument(string Json, string ETag, string ContentType)
public sealed record WellKnownValidationIssue(string Code, string Message, bool IsError);
public static class WellKnownBuilder
public static class WellKnownValidator
```

