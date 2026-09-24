namespace Dle.ContractTests.Wire;

/// <summary>
/// The SDK wire contract of §B.7.2, asserted against literal JSON.
/// </summary>
/// <remarks>
/// <para>
/// Three SDKs in three languages are written against these shapes, and none of them is compiled
/// against this assembly. A rename here is not caught by the compiler, is not caught by a round-trip
/// test — a serializer that renames a property still reads back what it wrote — and is not caught by
/// an integration test that uses the same DTO on both ends. It is caught by comparing the produced
/// bytes to the bytes in the specification, which is what these tests do.
/// </para>
/// <para>
/// The literals below are transcribed from §B.7.2. Where the specification abbreviates a value with
/// an ellipsis the value is filled in, but no property is added, removed or renamed. If one of these
/// assertions fails, the correct response is almost never to update the literal: it is to restore the
/// property, or to accept that the wire format has a new major version and every SDK needs a release.
/// </para>
/// </remarks>
public sealed class SdkWireContractTests
{
    /// <summary>The instant used everywhere a timestamp appears, so the literals stay comparable.</summary>
    private static readonly DateTimeOffset Moment =
        new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Contract", "B.7.2")]
    public void ResolveRequestDto_SerializesToTheSnakeCaseShapeOfB72()
    {
        ResolveRequestDto request = new()
        {
            InstallId = "9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1",
            Platform = "android",
            AppVersion = "3.4.1",
            OsVersion = "15",
            Referrer = "dl_cid%3DaB3xK9pQ%26utm_source%3Dfb",
            ClaimCode = null,
            Signals = new DeviceSignalsDto { Language = "sk-SK", Screen = "1080x2400", TzOffset = 120 },
            Consent = new ConsentDto { Analytics = true, Attribution = true, Ts = Moment },
        };

        const string expected =
            """
            {"install_id":"9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1","platform":"android","app_version":"3.4.1","os_version":"15","referrer":"dl_cid%3DaB3xK9pQ%26utm_source%3Dfb","signals":{"language":"sk-SK","screen":"1080x2400","tz_offset":120},"consent":{"analytics":true,"attribution":true,"ts":"2026-09-03T10:00:00+00:00"}}
            """;

        Assert.Equal(expected, JsonSerializer.Serialize(request, DleJson.Default));
    }

    [Fact]
    [Trait("Contract", "B.7.2")]
    public void ResolveResponseDto_SerializesToTheSnakeCaseShapeOfB72()
    {
        ResolveResponseDto response = new()
        {
            Matched = true,
            MatchType = "install_referrer",
            Confidence = 1.0m,
            ClickId = "aB3xK9pQ",
            Link = new ResolveLinkDto
            {
                Id = "7286414500000000001",
                DeeplinkPath = "/promo/jesen",
                Campaign = "jesen26",
            },
            Params = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["utm_source"] = "fb",
                ["utm_campaign"] = "jesen26",
                ["promo"] = "AUTUMN20",
            },
            ExpiresIn = 0,
        };

        const string expected =
            """
            {"matched":true,"match_type":"install_referrer","confidence":1.0,"click_id":"aB3xK9pQ","link":{"id":"7286414500000000001","deeplink_path":"/promo/jesen","campaign":"jesen26"},"params":{"utm_source":"fb","utm_campaign":"jesen26","promo":"AUTUMN20"},"expires_in":0}
            """;

        Assert.Equal(expected, JsonSerializer.Serialize(response, DleJson.Default));
    }

    [Fact]
    [Trait("Contract", "B.7.2")]
    public void EventBatchDto_SerializesToTheSnakeCaseShapeOfB72()
    {
        EventBatchDto batch = new()
        {
            InstallId = "9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1",
            Events =
            [
                new EventDto { Type = "link_open", Url = "https://link.zak.sk/aB3xK9pQ", Ts = Moment },
                new EventDto
                {
                    Type = "conversion",
                    Name = "purchase",
                    Value = 24.9m,
                    Currency = "EUR",
                    Ts = Moment.AddMinutes(5),
                },
            ],
        };

        const string expected =
            """
            {"install_id":"9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1","events":[{"type":"link_open","url":"https://link.zak.sk/aB3xK9pQ","ts":"2026-09-03T10:00:00+00:00"},{"type":"conversion","name":"purchase","value":24.9,"currency":"EUR","ts":"2026-09-03T10:05:00+00:00"}]}
            """;

        Assert.Equal(expected, JsonSerializer.Serialize(batch, DleJson.Default));
    }

    [Fact]
    [Trait("Contract", "B.7.2")]
    public void EventBatchAcceptedDto_SerializesToItsTwoCounters()
    {
        const string expected = """{"accepted":2,"rejected":0}""";

        Assert.Equal(
            expected,
            JsonSerializer.Serialize(new EventBatchAcceptedDto { Accepted = 2, Rejected = 0 }, DleJson.Default));
    }

    [Fact]
    [Trait("Contract", "B.7.2")]
    public void ResolveRequestDto_DeserializesTheExampleBodyFromTheSpecification()
    {
        const string body =
            """
            {
              "install_id": "9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1",
              "platform": "android",
              "app_version": "3.4.1",
              "os_version": "15",
              "referrer": "dl_cid%3DaB3xK9pQ%26utm_source%3Dfb",
              "claim_code": null,
              "signals": { "language": "sk-SK", "screen": "1080x2400", "tz_offset": 120 },
              "consent": { "analytics": true, "attribution": true, "ts": "2026-09-03T10:00:00Z" }
            }
            """;

        ResolveRequestDto? request = JsonSerializer.Deserialize<ResolveRequestDto>(body, DleJson.Default);

        Assert.NotNull(request);
        Assert.Equal("9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1", request.InstallId);
        Assert.Equal("android", request.Platform);
        Assert.Equal("3.4.1", request.AppVersion);
        Assert.Equal("15", request.OsVersion);
        Assert.Equal("dl_cid%3DaB3xK9pQ%26utm_source%3Dfb", request.Referrer);
        Assert.Null(request.ClaimCode);
        Assert.Equal("sk-SK", request.Signals?.Language);
        Assert.Equal("1080x2400", request.Signals?.Screen);
        Assert.Equal(120, request.Signals?.TzOffset);
        Assert.True(request.Consent?.Attribution);
        Assert.Equal(Moment, request.Consent?.Ts);
    }

    [Fact]
    [Trait("Contract", "B.7.2")]
    public void ResolveRequestDto_CamelCaseNames_AreNotAccepted()
    {
        // Property matching is case sensitive on purpose (DleJson.Default). An SDK that sends
        // installId must fail loudly rather than have the field silently dropped and then be told the
        // install could not be matched — which is the single most expensive kind of integration bug in
        // this product, because it looks like an attribution problem rather than a serialization one.
        const string body = """{"installId":"9f2c","platform":"android"}""";

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<ResolveRequestDto>(body, DleJson.Default));
    }

    [Fact]
    [Trait("Contract", "B.7.2")]
    public void ResolveResponseDto_OmitsAbsentOptionalMembers()
    {
        // An unmatched install is a 200 with matched=false, and the SDKs treat the absence of
        // click_id and link as "no context" rather than as a malformed body.
        ResolveResponseDto response = new()
        {
            Matched = false,
            MatchType = "none",
            Confidence = 0m,
        };

        const string expected = """{"matched":false,"match_type":"none","confidence":0,"params":{},"expires_in":0}""";

        Assert.Equal(expected, JsonSerializer.Serialize(response, DleJson.Default));
    }

    [Fact]
    [Trait("Contract", "B.7.2")]
    public void Params_KeysKeepTheirOriginalSpelling()
    {
        // The naming policy renames properties, never dictionary keys. UTM parameters and custom data
        // are values, and a policy that lower-cased or snake_cased them would corrupt the payload the
        // application is meant to act on.
        ResolveResponseDto response = new()
        {
            Matched = true,
            MatchType = "direct_open",
            Confidence = 1.0m,
            Params = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["utmSource"] = "fb",
                ["PromoCode"] = "AUTUMN20",
            },
        };

        string json = JsonSerializer.Serialize(response, DleJson.Default);

        Assert.Contains("\"utmSource\":\"fb\"", json, StringComparison.Ordinal);
        Assert.Contains("\"PromoCode\":\"AUTUMN20\"", json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Contract", "B.7.2")]
    public void EventBatch_MaximumSize_IsTheHundredEventsOfB72()
    {
        Assert.Equal(100, EventBatchDto.MaxEventsPerBatch);
    }

    [Theory]
    [Trait("Contract", "B.7.2")]
    [InlineData("""{"install_id":"a","platform":"ios",}""")]
    [InlineData("""{"install_id":"a","platform":"ios" /* comment */}""")]
    public void WireFormat_RejectsLenientJson(string body)
    {
        // Trailing commas and comments are refused. A wire format that accepts them accepts two
        // dialects, and the second one is only ever exercised by whichever client happens to emit it.
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<ResolveRequestDto>(body, DleJson.Default));
    }

    [Fact]
    [Trait("Contract", "B.7.2")]
    public void WireFormat_RejectsDeeplyNestedPayloads()
    {
        // A depth cap is the cheap half of the T-12 defence: a hostile payload cannot exhaust the
        // stack before any of the product's own validation has run.
        string nested = string.Concat(Enumerable.Repeat("""{"properties":""", 64))
            + "{}"
            + new string('}', 64);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EventDto>(nested, DleJson.Default));
    }
}
