using System.Collections.ObjectModel;
using Dle.Domain.Links;
using Dle.Domain.Privacy;
using Dle.Domain.Routing;

namespace Dle.UnitTests.Links;

/// <summary>
/// Builders for <see cref="LinkSnapshot"/>. Every required member is given a boring, valid value so
/// that a test only has to state the one field it is actually about.
/// </summary>
internal static class TestLinks
{
    public static readonly Guid TenantId = new("11111111-1111-1111-1111-111111111111");
    public static readonly Guid DomainId = new("22222222-2222-2222-2222-222222222222");

    public static LinkSnapshot Snapshot(
        string targetUrl = "https://shop.example.com/product/123",
        string? deeplinkPath = null,
        IReadOnlyDictionary<string, string>? utm = null,
        IReadOnlyList<RoutingRule>? rules = null,
        bool isActive = true,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? quarantinedAt = null,
        string? expiredUrl = null,
        string? iosStoreUrl = null,
        string? androidStoreUrl = null,
        string? iosCustomScheme = null,
        string? androidCustomScheme = null,
        ConsentMode tenantConsentMode = ConsentMode.Full,
        ConsentMode? domainConsentMode = null,
        string slug = "abc123",
        long id = 42) => new()
        {
            Id = id,
            TenantId = TenantId,
            DomainId = DomainId,
            Slug = slug,
            TargetUrl = targetUrl,
            DeeplinkPath = deeplinkPath,
            RoutingRules = rules ?? [],
            Og = OgMeta.Empty,
            Utm = utm ?? ReadOnlyDictionary<string, string>.Empty,
            IsActive = isActive,
            StartsAt = startsAt,
            ExpiresAt = expiresAt,
            QuarantinedAt = quarantinedAt,
            ExpiredUrl = expiredUrl,
            IosStoreUrl = iosStoreUrl,
            AndroidStoreUrl = androidStoreUrl,
            IosCustomScheme = iosCustomScheme,
            AndroidCustomScheme = androidCustomScheme,
            TenantConsentMode = tenantConsentMode,
            DomainConsentMode = domainConsentMode,
        };

    public static IReadOnlyDictionary<string, string> Map(params (string Key, string Value)[] pairs)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in pairs)
        {
            dictionary[key] = value;
        }

        return new ReadOnlyDictionary<string, string>(dictionary);
    }
}
