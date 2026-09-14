using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Dle.Domain.WellKnown;

/// <summary>
/// Pure generators for <c>apple-app-site-association</c> and <c>assetlinks.json</c>
/// (§C.3.3, FR-141, FR-142). No HTTP, no I/O, no clock — everything here is testable in isolation.
/// </summary>
/// <remarks>
/// Both builders return <see langword="null"/> when a host has no application of that platform, and the
/// endpoint must turn that into <c>404</c>. Returning an empty but syntactically valid JSON document
/// instead is the trap TC-122 exists for: Apple accepts such a file, caches it, and the domain is dead
/// for roughly a week.
/// </remarks>
public static class WellKnownBuilder
{
    private const string HandleAllUrlsRelation = "delegate_permission/common.handle_all_urls";
    private const string AndroidAppNamespace = "android_app";
    private const int MaxFingerprintLength = 128;

    private static readonly IReadOnlyList<string> HandleAllUrls = [HandleAllUrlsRelation];

    /// <summary>
    /// The component set applied to an application that does not declare its own.
    /// </summary>
    /// <remarks>
    /// Apple evaluates components in order, so the exclusions come first. Excluding
    /// <c>/.well-known/*</c> matters in particular: without it, a device fetching the association file
    /// itself can be bounced into the application. The <c>no_dl</c> fragment is the documented per-link
    /// opt-out that lets a user or an operator reach the web page on purpose.
    /// </remarks>
    public static IReadOnlyList<AasaComponent> DefaultComponents { get; } =
    [
        new AasaComponent { Fragment = "no_dl", Exclude = true, Comment = "Opt-out fragment: never open the application." },
        new AasaComponent { Path = "/.well-known/*", Exclude = true, Comment = "Never open the application on well-known files." },
        new AasaComponent { Path = "/api/*", Exclude = true, Comment = "API surface is not a deep link." },
        new AasaComponent { Path = "/healthz", Exclude = true, Comment = "Health probe is not a deep link." },
        new AasaComponent { Path = "/*", Comment = "All other paths." },
    ];

    /// <summary>
    /// Builds the <c>apple-app-site-association</c> document for one host (FR-141).
    /// </summary>
    /// <param name="apps">The iOS applications registered on the host.</param>
    /// <returns>
    /// The document, or <see langword="null"/> when no usable application is registered — in which case
    /// the endpoint must answer <c>404</c> rather than an empty document (TC-122).
    /// </returns>
    public static WellKnownDocument? BuildAasa(IReadOnlyList<AasaAppEntry> apps)
    {
        if (apps is null || apps.Count == 0)
        {
            return null;
        }

        var details = new List<AasaDetail>(apps.Count);
        var appClips = new List<string>();

        foreach (AasaAppEntry app in apps)
        {
            if (app is null || string.IsNullOrWhiteSpace(app.AppId))
            {
                continue;
            }

            IReadOnlyList<AasaComponent> components =
                app.Components is { Count: > 0 } declared ? declared : DefaultComponents;

            details.Add(new AasaDetail
            {
                AppIds = [app.AppId.Trim()],
                Components = components,
            });

            if (!string.IsNullOrWhiteSpace(app.AppClipAppId))
            {
                appClips.Add(app.AppClipAppId.Trim());
            }
        }

        if (details.Count == 0)
        {
            return null;
        }

        var document = new AasaDocument
        {
            Applinks = new AasaApplinks { Details = details },
            AppClips = appClips.Count == 0 ? null : new AasaAppClips { Apps = appClips },
        };

        return Package(JsonSerializer.Serialize(document, DleJson.WellKnown));
    }

    /// <summary>
    /// Builds the <c>assetlinks.json</c> document for one host (FR-142).
    /// </summary>
    /// <param name="apps">The Android applications registered on the host.</param>
    /// <returns>
    /// The document, or <see langword="null"/> when no usable application is registered — in which case
    /// the endpoint must answer <c>404</c>.
    /// </returns>
    /// <remarks>
    /// Dynamic components are written under <c>relation_extensions</c> using Apple's <c>"/"</c> and
    /// <c>"#"</c> keys, which is what Android 15+ expects. The older <c>pathPattern</c> notation is never
    /// emitted: it parses, it validates, and it silently does nothing (§C.3.3).
    /// </remarks>
    public static WellKnownDocument? BuildAssetLinks(IReadOnlyList<AndroidAppEntry> apps)
    {
        if (apps is null || apps.Count == 0)
        {
            return null;
        }

        var statements = new List<AssetLinkStatement>(apps.Count);

        foreach (AndroidAppEntry app in apps)
        {
            if (app is null || string.IsNullOrWhiteSpace(app.PackageName) || app.Sha256CertFingerprints is null)
            {
                continue;
            }

            var fingerprints = new List<string>(app.Sha256CertFingerprints.Count);

            foreach (string fingerprint in app.Sha256CertFingerprints)
            {
                string normalized = NormalizeFingerprint(fingerprint);

                if (normalized.Length > 0)
                {
                    fingerprints.Add(normalized);
                }
            }

            if (fingerprints.Count == 0)
            {
                continue;
            }

            statements.Add(new AssetLinkStatement
            {
                Relation = HandleAllUrls,
                Target = new AssetLinkTarget
                {
                    Namespace = AndroidAppNamespace,
                    PackageName = app.PackageName.Trim(),
                    Sha256CertFingerprints = fingerprints,
                },
                RelationExtensions = app.DynamicComponents is { Count: > 0 } dynamicComponents
                    ? new Dictionary<string, AssetLinkRelationExtension>(StringComparer.Ordinal)
                    {
                        [HandleAllUrlsRelation] = new() { DynamicAppLinkComponents = dynamicComponents },
                    }
                    : null,
            });
        }

        if (statements.Count == 0)
        {
            return null;
        }

        return Package(JsonSerializer.Serialize(statements, DleJson.WellKnown));
    }

    /// <summary>
    /// Normalizes a SHA-256 certificate fingerprint to the colon-separated upper-case form Google
    /// publishes and expects, accepting the bare 64-character hex form that keytool also prints.
    /// </summary>
    private static string NormalizeFingerprint(string? fingerprint)
    {
        // 32 bytes rendered as "AA:BB:…" is 95 characters; anything materially longer is not a
        // SHA-256 fingerprint and is rejected rather than stack-allocated.
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length > MaxFingerprintLength)
        {
            return string.Empty;
        }

        Span<char> hex = stackalloc char[fingerprint.Length];
        int length = 0;

        foreach (char c in fingerprint)
        {
            if (char.IsAsciiHexDigit(c))
            {
                hex[length++] = char.ToUpperInvariant(c);
            }
        }

        if (length == 0 || (length & 1) == 1)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + (length / 2) - 1);

        for (int i = 0; i < length; i += 2)
        {
            if (i > 0)
            {
                builder.Append(':');
            }

            builder.Append(hex[i]).Append(hex[i + 1]);
        }

        return builder.ToString();
    }

    private static WellKnownDocument Package(string json) =>
        new(json, ComputeETag(json), WellKnownDocument.JsonContentType);

    private static string ComputeETag(string json)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return string.Concat("\"", Base64Url.EncodeToString(hash), "\"");
    }

    // ------------------------------------------------------------------ wire shapes
    //
    // These exist only to pin the exact JSON Apple and Google require. Every key is spelled out with
    // JsonPropertyName so that no serializer naming policy can rewrite "appIDs" or "package_name".

    private sealed record AasaDocument
    {
        [JsonPropertyName("applinks")]
        public required AasaApplinks Applinks { get; init; }

        [JsonPropertyName("appclips")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AasaAppClips? AppClips { get; init; }
    }

    private sealed record AasaApplinks
    {
        [JsonPropertyName("details")]
        public required IReadOnlyList<AasaDetail> Details { get; init; }
    }

    private sealed record AasaDetail
    {
        [JsonPropertyName("appIDs")]
        public required IReadOnlyList<string> AppIds { get; init; }

        [JsonPropertyName("components")]
        public required IReadOnlyList<AasaComponent> Components { get; init; }
    }

    private sealed record AasaAppClips
    {
        [JsonPropertyName("apps")]
        public required IReadOnlyList<string> Apps { get; init; }
    }

    private sealed record AssetLinkStatement
    {
        [JsonPropertyName("relation")]
        public required IReadOnlyList<string> Relation { get; init; }

        [JsonPropertyName("target")]
        public required AssetLinkTarget Target { get; init; }

        [JsonPropertyName("relation_extensions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyDictionary<string, AssetLinkRelationExtension>? RelationExtensions { get; init; }
    }

    private sealed record AssetLinkTarget
    {
        [JsonPropertyName("namespace")]
        public required string Namespace { get; init; }

        [JsonPropertyName("package_name")]
        public required string PackageName { get; init; }

        [JsonPropertyName("sha256_cert_fingerprints")]
        public required IReadOnlyList<string> Sha256CertFingerprints { get; init; }
    }

    private sealed record AssetLinkRelationExtension
    {
        [JsonPropertyName("dynamic_app_link_components")]
        public required IReadOnlyList<AasaComponent> DynamicAppLinkComponents { get; init; }
    }
}
