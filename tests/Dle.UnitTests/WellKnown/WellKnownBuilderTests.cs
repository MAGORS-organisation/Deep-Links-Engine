using System.Text.Json;
using Dle.Domain.WellKnown;
using Xunit;

namespace Dle.UnitTests.WellKnown;

/// <summary>
/// FR-141, FR-142, TC-122. Apple and Google read these files literally: the keys "/", "#", "?" and
/// "appIDs" are the format, not a naming style, and no serializer policy may rewrite them. An empty
/// document is worse than a 404 — Apple caches an empty association for about a week.
/// </summary>
public sealed class WellKnownBuilderTests
{
    private static readonly AasaAppEntry IosApp = new() { AppId = "ABCDE12345.sk.zakaznik.app" };

    private static readonly AndroidAppEntry AndroidApp = new()
    {
        PackageName = "sk.zakaznik.app",
        Sha256CertFingerprints = ["AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99"],
    };

    // ================================================================ TC-122: nothing means nothing

    [Fact]
    [Trait("TestCase", "TC-122")]
    public void BuildAasa_NoIosApplication_ReturnsNullSoTheEdgeCanAnswer404()
    {
        Assert.Null(WellKnownBuilder.BuildAasa([]));
        Assert.Null(WellKnownBuilder.BuildAasa(null!));
    }

    [Fact]
    [Trait("TestCase", "TC-122")]
    public void BuildAasa_OnlyUnusableEntries_ReturnsNullRatherThanAnEmptyDocument()
    {
        AasaAppEntry[] apps = [null!, new AasaAppEntry { AppId = "   " }];

        Assert.Null(WellKnownBuilder.BuildAasa(apps));
    }

    [Fact]
    [Trait("TestCase", "TC-122")]
    public void BuildAssetLinks_NoAndroidApplication_ReturnsNull()
    {
        Assert.Null(WellKnownBuilder.BuildAssetLinks([]));
        Assert.Null(WellKnownBuilder.BuildAssetLinks(null!));
    }

    [Fact]
    public void BuildAssetLinks_EntryWithoutAUsableFingerprint_IsDroppedAndYieldsNull()
    {
        AndroidAppEntry[] apps =
        [
            new AndroidAppEntry { PackageName = "sk.zakaznik.app", Sha256CertFingerprints = [] },
            new AndroidAppEntry { PackageName = "sk.zakaznik.app", Sha256CertFingerprints = ["zz:zz"] },
            new AndroidAppEntry { PackageName = "  ", Sha256CertFingerprints = ["AABB"] },
        ];

        Assert.Null(WellKnownBuilder.BuildAssetLinks(apps));
    }

    // ================================================================ AASA shape

    [Fact]
    [Trait("TestCase", "TC-121")]
    public void BuildAasa_UsesTheLiteralAppleKeys()
    {
        WellKnownDocument document = Require(WellKnownBuilder.BuildAasa([IosApp]));

        Assert.Contains("\"applinks\"", document.Json, StringComparison.Ordinal);
        Assert.Contains("\"details\"", document.Json, StringComparison.Ordinal);
        Assert.Contains("\"appIDs\"", document.Json, StringComparison.Ordinal);
        Assert.Contains("\"components\"", document.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"app_ids\"", document.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"appids\"", document.Json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestCase", "TC-121")]
    public void BuildAasa_ComponentKeysSurviveSerialisationVerbatim()
    {
        AasaAppEntry app = IosApp with
        {
            Components =
            [
                new AasaComponent
                {
                    Path = "/product/*",
                    Fragment = "no_dl",
                    Query = new Dictionary<string, string>(StringComparer.Ordinal) { ["utm_source"] = "poster" },
                    Exclude = false,
                    CaseSensitive = true,
                    PercentEncoded = false,
                    Comment = "Product pages only.",
                },
            ],
        };

        WellKnownDocument document = Require(WellKnownBuilder.BuildAasa([app]));

        // The raw bytes, not only the parsed tree: an encoder that escaped these keys would still
        // parse back correctly here and still be rejected by Apple.
        Assert.Contains("\"/\":", document.Json, StringComparison.Ordinal);
        Assert.Contains("\"#\":", document.Json, StringComparison.Ordinal);
        Assert.Contains("\"?\":", document.Json, StringComparison.Ordinal);

        using JsonDocument parsed = JsonDocument.Parse(document.Json);
        JsonElement component = parsed.RootElement
            .GetProperty("applinks")
            .GetProperty("details")[0]
            .GetProperty("components")[0];

        Assert.Equal("/product/*", component.GetProperty("/").GetString());
        Assert.Equal("no_dl", component.GetProperty("#").GetString());
        Assert.Equal("poster", component.GetProperty("?").GetProperty("utm_source").GetString());
        Assert.False(component.GetProperty("exclude").GetBoolean());
        Assert.True(component.GetProperty("caseSensitive").GetBoolean());
        Assert.False(component.GetProperty("percentEncoded").GetBoolean());
        Assert.Equal("Product pages only.", component.GetProperty("comment").GetString());
    }

    [Fact]
    public void BuildAasa_ComponentWithOnlyAPath_OmitsTheOtherKeysRatherThanEmittingNulls()
    {
        AasaAppEntry app = IosApp with { Components = [new AasaComponent { Path = "/*" }] };

        WellKnownDocument document = Require(WellKnownBuilder.BuildAasa([app]));

        using JsonDocument parsed = JsonDocument.Parse(document.Json);
        JsonElement component = parsed.RootElement
            .GetProperty("applinks")
            .GetProperty("details")[0]
            .GetProperty("components")[0];

        Assert.Equal("/*", component.GetProperty("/").GetString());
        Assert.False(component.TryGetProperty("#", out _));
        Assert.False(component.TryGetProperty("?", out _));
        Assert.False(component.TryGetProperty("exclude", out _));
        Assert.DoesNotContain("null", document.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAasa_AppWithoutComponents_GetsTheDefaultComponentSet()
    {
        WellKnownDocument document = Require(WellKnownBuilder.BuildAasa([IosApp]));

        using JsonDocument parsed = JsonDocument.Parse(document.Json);
        JsonElement components = parsed.RootElement
            .GetProperty("applinks")
            .GetProperty("details")[0]
            .GetProperty("components");

        Assert.Equal(WellKnownBuilder.DefaultComponents.Count, components.GetArrayLength());
    }

    [Fact]
    public void DefaultComponents_ExcludeTheEdgesOwnRoutesAndTheOptOutFragment()
    {
        IReadOnlyList<AasaComponent> components = WellKnownBuilder.DefaultComponents;

        Assert.Contains(components, c => c.Fragment == "no_dl" && c.Exclude == true);
        Assert.Contains(components, c => c.Path == "/.well-known/*" && c.Exclude == true);
        Assert.Contains(components, c => c.Path == "/api/*" && c.Exclude == true);
        Assert.Contains(components, c => c.Path == "/healthz" && c.Exclude == true);
        Assert.Contains(components, c => c.Path == "/*" && c.Exclude != true);
    }

    [Fact]
    public void DefaultComponents_PutTheCatchAllLast()
    {
        // Apple evaluates components in order; a leading "/*" would swallow every exclusion.
        IReadOnlyList<AasaComponent> components = WellKnownBuilder.DefaultComponents;

        Assert.Equal("/*", components[^1].Path);
    }

    [Fact]
    public void BuildAasa_SeveralApplications_ProducesOneDetailEntryEach()
    {
        AasaAppEntry[] apps =
        [
            IosApp,
            new AasaAppEntry { AppId = "ABCDE12345.sk.zakaznik.other" },
        ];

        WellKnownDocument document = Require(WellKnownBuilder.BuildAasa(apps));

        using JsonDocument parsed = JsonDocument.Parse(document.Json);
        JsonElement details = parsed.RootElement.GetProperty("applinks").GetProperty("details");

        Assert.Equal(2, details.GetArrayLength());
        Assert.Equal("ABCDE12345.sk.zakaznik.app", details[0].GetProperty("appIDs")[0].GetString());
        Assert.Equal("ABCDE12345.sk.zakaznik.other", details[1].GetProperty("appIDs")[0].GetString());
    }

    [Fact]
    [Trait("TestCase", "FR-146")]
    public void BuildAasa_AppClip_IsDeclaredInItsOwnSection()
    {
        AasaAppEntry app = IosApp with { AppClipAppId = "ABCDE12345.sk.zakaznik.app.Clip" };

        WellKnownDocument document = Require(WellKnownBuilder.BuildAasa([app]));

        using JsonDocument parsed = JsonDocument.Parse(document.Json);
        Assert.Equal(
            "ABCDE12345.sk.zakaznik.app.Clip",
            parsed.RootElement.GetProperty("appclips").GetProperty("apps")[0].GetString());
    }

    [Fact]
    public void BuildAasa_WithoutAnAppClip_OmitsTheAppClipsSection()
    {
        WellKnownDocument document = Require(WellKnownBuilder.BuildAasa([IosApp]));

        using JsonDocument parsed = JsonDocument.Parse(document.Json);
        Assert.False(parsed.RootElement.TryGetProperty("appclips", out _));
    }

    // ================================================================ assetlinks shape

    [Fact]
    public void BuildAssetLinks_UsesTheLiteralGoogleKeys()
    {
        WellKnownDocument document = Require(WellKnownBuilder.BuildAssetLinks([AndroidApp]));

        using JsonDocument parsed = JsonDocument.Parse(document.Json);
        JsonElement statement = parsed.RootElement[0];

        Assert.Equal(JsonValueKind.Array, parsed.RootElement.ValueKind);
        Assert.Equal("delegate_permission/common.handle_all_urls", statement.GetProperty("relation")[0].GetString());
        Assert.Equal("android_app", statement.GetProperty("target").GetProperty("namespace").GetString());
        Assert.Equal("sk.zakaznik.app", statement.GetProperty("target").GetProperty("package_name").GetString());
        Assert.Equal(1, statement.GetProperty("target").GetProperty("sha256_cert_fingerprints").GetArrayLength());
    }

    [Theory]
    [InlineData("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899")]
    [InlineData("AA BB CC DD EE FF 00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF 00 11 22 33 44 55 66 77 88 99")]
    [InlineData("aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99")]
    public void BuildAssetLinks_FingerprintSpelling_IsNormalisedToUppercaseColonSeparatedPairs(string fingerprint)
    {
        AndroidAppEntry app = AndroidApp with { Sha256CertFingerprints = [fingerprint] };

        WellKnownDocument document = Require(WellKnownBuilder.BuildAssetLinks([app]));

        using JsonDocument parsed = JsonDocument.Parse(document.Json);
        Assert.Equal(
            "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99",
            parsed.RootElement[0].GetProperty("target").GetProperty("sha256_cert_fingerprints")[0].GetString());
    }

    [Fact]
    [Trait("TestCase", "FR-142")]
    public void BuildAssetLinks_DynamicComponents_UseTheSameLiteralKeysAndNeverPathPattern()
    {
        AndroidAppEntry app = AndroidApp with
        {
            DynamicComponents =
            [
                new AasaComponent { Path = "/product/*", Comment = "Product pages." },
                new AasaComponent { Fragment = "no_dl", Exclude = true },
            ],
        };

        WellKnownDocument document = Require(WellKnownBuilder.BuildAssetLinks([app]));

        Assert.DoesNotContain("pathPattern", document.Json, StringComparison.OrdinalIgnoreCase);

        using JsonDocument parsed = JsonDocument.Parse(document.Json);
        JsonElement components = parsed.RootElement[0]
            .GetProperty("relation_extensions")
            .GetProperty("delegate_permission/common.handle_all_urls")
            .GetProperty("dynamic_app_link_components");

        Assert.Equal(2, components.GetArrayLength());
        Assert.Equal("/product/*", components[0].GetProperty("/").GetString());
        Assert.Equal("no_dl", components[1].GetProperty("#").GetString());
        Assert.True(components[1].GetProperty("exclude").GetBoolean());
    }

    [Fact]
    public void BuildAssetLinks_WithoutDynamicComponents_OmitsTheRelationExtensions()
    {
        WellKnownDocument document = Require(WellKnownBuilder.BuildAssetLinks([AndroidApp]));

        using JsonDocument parsed = JsonDocument.Parse(document.Json);
        Assert.False(parsed.RootElement[0].TryGetProperty("relation_extensions", out _));
    }

    // ================================================================ ETag and content type

    [Fact]
    [Trait("TestCase", "TC-125")]
    public void BuildAasa_IdenticalInput_ProducesTheSameETag()
    {
        WellKnownDocument first = Require(WellKnownBuilder.BuildAasa([IosApp]));
        WellKnownDocument second = Require(WellKnownBuilder.BuildAasa([IosApp]));

        Assert.Equal(first.ETag, second.ETag);
        Assert.Equal(first.Json, second.Json);
    }

    [Fact]
    [Trait("TestCase", "TC-125")]
    public void BuildAasa_DifferentInput_ProducesADifferentETag()
    {
        WellKnownDocument first = Require(WellKnownBuilder.BuildAasa([IosApp]));
        WellKnownDocument second = Require(WellKnownBuilder.BuildAasa([IosApp with { AppId = "ABCDE12345.sk.zakaznik.other" }]));

        Assert.NotEqual(first.ETag, second.ETag);
    }

    [Fact]
    public void BuildAssetLinks_DifferentFingerprint_ProducesADifferentETag()
    {
        AndroidAppEntry other = AndroidApp with
        {
            Sha256CertFingerprints = ["11:22:33:44:55:66:77:88:99:00:AA:BB:CC:DD:EE:FF:11:22:33:44:55:66:77:88:99:00:AA:BB:CC:DD:EE:FF"],
        };

        Assert.NotEqual(
            Require(WellKnownBuilder.BuildAssetLinks([AndroidApp])).ETag,
            Require(WellKnownBuilder.BuildAssetLinks([other])).ETag);
    }

    [Fact]
    public void Build_ETagIsAQuotedOpaqueToken()
    {
        WellKnownDocument document = Require(WellKnownBuilder.BuildAasa([IosApp]));

        Assert.StartsWith("\"", document.ETag, StringComparison.Ordinal);
        Assert.EndsWith("\"", document.ETag, StringComparison.Ordinal);
        Assert.True(document.ETag.Length > 2);
    }

    [Fact]
    public void Build_ContentTypeIsApplicationJson()
    {
        Assert.Equal("application/json", Require(WellKnownBuilder.BuildAasa([IosApp])).ContentType);
        Assert.Equal("application/json", Require(WellKnownBuilder.BuildAssetLinks([AndroidApp])).ContentType);
        Assert.Equal("application/json", WellKnownDocument.JsonContentType);
    }

    [Fact]
    public void Build_OutputIsValidJsonThatTheValidatorAccepts()
    {
        WellKnownDocument aasa = Require(WellKnownBuilder.BuildAasa([IosApp]));
        WellKnownDocument assetLinks = Require(WellKnownBuilder.BuildAssetLinks([AndroidApp]));

        Assert.Empty(WellKnownValidator.ValidateAasa(
            aasa.Json, aasa.ContentType, 200, 0, [IosApp.AppId]));
        Assert.Empty(WellKnownValidator.ValidateAssetLinks(
            assetLinks.Json, assetLinks.ContentType, 200, 0, AndroidApp.Sha256CertFingerprints));
    }

    private static WellKnownDocument Require(WellKnownDocument? document)
    {
        Assert.NotNull(document);
        return document;
    }
}
