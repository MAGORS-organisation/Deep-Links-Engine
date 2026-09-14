namespace Dle.Domain.WellKnown;

/// <summary>
/// Validates a well-known file actually fetched from a customer domain (FR-143, FR-144).
/// </summary>
/// <remarks>
/// <para>
/// The validator is deliberately strict about transport, because Apple and Google are. A redirect on
/// the way to the file is fatal on both platforms even though the file itself is perfectly valid, and a
/// content type other than <c>application/json</c> is enough for Apple to discard the association
/// (§A.2.1, §A.2.2, TC-121). Reporting these as warnings would mean shipping a domain that the operator
/// believes is verified and that no device will honour.
/// </para>
/// <para>
/// The one finding that is a warning rather than an error is the Play App Signing heuristic: an upload
/// certificate fingerprint produces a file that is syntactically correct and works in a debug build, so
/// the validator can only say that it looks wrong (FR-144, TC-123).
/// </para>
/// </remarks>
public static class WellKnownValidator
{
    /// <summary>The file was reached through at least one redirect. Fatal on iOS and Android alike.</summary>
    public const string ErrRedirect = "well_known.redirect";

    /// <summary>The response content type is not <c>application/json</c>.</summary>
    public const string ErrContentType = "well_known.content_type";

    /// <summary>The response status was not <c>200</c>.</summary>
    public const string ErrStatus = "well_known.status";

    /// <summary>The body is not JSON, or is JSON of the wrong shape.</summary>
    public const string ErrMalformed = "well_known.malformed";

    /// <summary>An app ID or certificate fingerprint that was expected is not declared in the file.</summary>
    public const string ErrAppIdMismatch = "well_known.appid_mismatch";

    /// <summary>A declared fingerprint looks like an upload certificate rather than the Play App Signing certificate.</summary>
    public const string WarnUploadCertificate = "well_known.upload_certificate";

    private const string HandleAllUrlsRelation = "delegate_permission/common.handle_all_urls";
    private const string AndroidAppNamespace = "android_app";
    private const int MaxFingerprintLength = 128;

    private static readonly IReadOnlyList<string> NoFingerprints = [];

    /// <summary>
    /// Validates a fetched <c>apple-app-site-association</c> file.
    /// </summary>
    /// <param name="body">The response body, exactly as received.</param>
    /// <param name="contentType">The response <c>Content-Type</c> header; a charset parameter is ignored.</param>
    /// <param name="statusCode">The final HTTP status code.</param>
    /// <param name="redirectCount">How many redirects were followed to reach the file. Anything above zero is fatal.</param>
    /// <param name="expectedAppIds">App IDs that must be declared, for example <c>ABCDE12345.sk.zakaznik.app</c>.</param>
    /// <returns>Every finding, most severe first; empty when the file is good.</returns>
    public static IReadOnlyList<WellKnownValidationIssue> ValidateAasa(
        string? body,
        string? contentType,
        int statusCode,
        int redirectCount,
        IReadOnlyList<string> expectedAppIds)
    {
        var issues = new List<WellKnownValidationIssue>();

        if (!ValidateTransport(body, contentType, statusCode, redirectCount, issues))
        {
            return issues;
        }

        List<string> declared;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body!);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("applinks", out JsonElement applinks)
                || applinks.ValueKind != JsonValueKind.Object
                || !applinks.TryGetProperty("details", out JsonElement details)
                || details.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new WellKnownValidationIssue(
                    ErrMalformed,
                    "The file must be a JSON object containing \"applinks\" with a \"details\" array.",
                    IsError: true));

                return issues;
            }

            declared = CollectAppIds(details);
        }
        catch (JsonException ex)
        {
            issues.Add(new WellKnownValidationIssue(
                ErrMalformed,
                FormattableString.Invariant($"The file is not valid JSON: {ex.Message}"),
                IsError: true));

            return issues;
        }

        if (expectedAppIds is not null)
        {
            foreach (string expected in expectedAppIds)
            {
                if (string.IsNullOrWhiteSpace(expected))
                {
                    continue;
                }

                if (!ContainsInvariant(declared, expected.Trim()))
                {
                    issues.Add(new WellKnownValidationIssue(
                        ErrAppIdMismatch,
                        FormattableString.Invariant(
                            $"App ID \"{expected}\" is not declared in the file; Universal Links will not open the application."),
                        IsError: true));
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Validates a fetched <c>assetlinks.json</c> file.
    /// </summary>
    /// <param name="body">The response body, exactly as received.</param>
    /// <param name="contentType">The response <c>Content-Type</c> header; a charset parameter is ignored.</param>
    /// <param name="statusCode">The final HTTP status code.</param>
    /// <param name="redirectCount">How many redirects were followed to reach the file. Anything above zero is fatal.</param>
    /// <param name="expectedFingerprints">SHA-256 certificate fingerprints that must be declared.</param>
    /// <returns>Every finding; empty when the file is good.</returns>
    public static IReadOnlyList<WellKnownValidationIssue> ValidateAssetLinks(
        string? body,
        string? contentType,
        int statusCode,
        int redirectCount,
        IReadOnlyList<string> expectedFingerprints) =>
        ValidateAssetLinks(body, contentType, statusCode, redirectCount, expectedFingerprints, NoFingerprints);

    /// <summary>
    /// Validates a fetched <c>assetlinks.json</c> file and, when the Play App Signing fingerprints for the
    /// application are known, also warns about fingerprints that look like an upload certificate
    /// (FR-144, TC-123).
    /// </summary>
    /// <param name="body">The response body, exactly as received.</param>
    /// <param name="contentType">The response <c>Content-Type</c> header; a charset parameter is ignored.</param>
    /// <param name="statusCode">The final HTTP status code.</param>
    /// <param name="redirectCount">How many redirects were followed to reach the file. Anything above zero is fatal.</param>
    /// <param name="expectedFingerprints">SHA-256 certificate fingerprints that must be declared.</param>
    /// <param name="knownPlayFingerprints">
    /// The fingerprints Play App Signing reports for the application. When empty, the heuristic is skipped.
    /// </param>
    /// <returns>Every finding; empty when the file is good.</returns>
    public static IReadOnlyList<WellKnownValidationIssue> ValidateAssetLinks(
        string? body,
        string? contentType,
        int statusCode,
        int redirectCount,
        IReadOnlyList<string> expectedFingerprints,
        IReadOnlyList<string> knownPlayFingerprints)
    {
        var issues = new List<WellKnownValidationIssue>();

        if (!ValidateTransport(body, contentType, statusCode, redirectCount, issues))
        {
            return issues;
        }

        List<string> declared;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body!);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new WellKnownValidationIssue(
                    ErrMalformed,
                    "The file must be a JSON array of statements.",
                    IsError: true));

                return issues;
            }

            declared = CollectFingerprints(document.RootElement);

            if (declared.Count == 0)
            {
                issues.Add(new WellKnownValidationIssue(
                    ErrMalformed,
                    "No statement declares the \"delegate_permission/common.handle_all_urls\" relation for an "
                    + "\"android_app\" target with SHA-256 certificate fingerprints.",
                    IsError: true));

                return issues;
            }
        }
        catch (JsonException ex)
        {
            issues.Add(new WellKnownValidationIssue(
                ErrMalformed,
                FormattableString.Invariant($"The file is not valid JSON: {ex.Message}"),
                IsError: true));

            return issues;
        }

        if (expectedFingerprints is not null)
        {
            foreach (string expected in expectedFingerprints)
            {
                string normalized = NormalizeFingerprint(expected);

                if (normalized.Length == 0)
                {
                    continue;
                }

                if (!declared.Contains(normalized, StringComparer.Ordinal))
                {
                    issues.Add(new WellKnownValidationIssue(
                        ErrAppIdMismatch,
                        FormattableString.Invariant(
                            $"Certificate fingerprint \"{expected}\" is not declared in the file; App Links will not be verified."),
                        IsError: true));
                }
            }
        }

        foreach (string fingerprint in declared)
        {
            if (LooksLikeUploadCertificate(fingerprint, knownPlayFingerprints))
            {
                issues.Add(new WellKnownValidationIssue(
                    WarnUploadCertificate,
                    FormattableString.Invariant(
                        $"Fingerprint \"{fingerprint}\" is not one of the Play App Signing certificates for this application. It is most likely the upload certificate, which works in a debug build and fails on devices installing from Play."),
                    IsError: false));
            }
        }

        return issues;
    }

    /// <summary>
    /// Heuristic for the most common Android App Links failure: the file declares the upload certificate
    /// from the local keystore instead of the certificate Google signs the released artifact with
    /// (§A.2.2, FR-144, TC-123).
    /// </summary>
    /// <param name="fingerprint">The fingerprint found in the file.</param>
    /// <param name="knownPlayFingerprints">
    /// The fingerprints Play App Signing reports. When this list is empty nothing can be concluded and the
    /// result is <see langword="false"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the fingerprint is absent from a non-empty set of known Play
    /// fingerprints. Comparison ignores case and any separators.
    /// </returns>
    public static bool LooksLikeUploadCertificate(string fingerprint, IReadOnlyList<string> knownPlayFingerprints)
    {
        if (knownPlayFingerprints is null || knownPlayFingerprints.Count == 0)
        {
            return false;
        }

        string candidate = NormalizeFingerprint(fingerprint);

        if (candidate.Length == 0)
        {
            return false;
        }

        foreach (string known in knownPlayFingerprints)
        {
            if (string.Equals(NormalizeFingerprint(known), candidate, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks status, redirects and content type. Returns <see langword="false"/> when the body cannot be
    /// meaningfully parsed, so the caller stops rather than piling a parse error on top of a 404.
    /// </summary>
    private static bool ValidateTransport(
        string? body,
        string? contentType,
        int statusCode,
        int redirectCount,
        List<WellKnownValidationIssue> issues)
    {
        bool usable = true;

        if (statusCode != 200)
        {
            issues.Add(new WellKnownValidationIssue(
                ErrStatus,
                FormattableString.Invariant($"The file must be served with status 200; the domain answered {statusCode}."),
                IsError: true));

            usable = false;
        }

        if (redirectCount > 0)
        {
            issues.Add(new WellKnownValidationIssue(
                ErrRedirect,
                FormattableString.Invariant(
                    $"The file was reached through {redirectCount} redirect(s). Neither Apple nor Google follows a redirect to a well-known file, so the association is ignored."),
                IsError: true));

            usable = false;
        }

        if (!IsJsonContentType(contentType))
        {
            issues.Add(new WellKnownValidationIssue(
                ErrContentType,
                FormattableString.Invariant(
                    $"The content type must be \"application/json\"; the domain answered \"{contentType ?? "(none)"}\"."),
                IsError: true));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            issues.Add(new WellKnownValidationIssue(ErrMalformed, "The response body is empty.", IsError: true));
            usable = false;
        }

        return usable;
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        int semicolon = contentType.IndexOf(';');
        ReadOnlySpan<char> mediaType = semicolon < 0 ? contentType.AsSpan() : contentType.AsSpan(0, semicolon);

        return mediaType.Trim().Equals(WellKnownDocument.JsonContentType, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> CollectAppIds(JsonElement details)
    {
        var appIds = new List<string>();

        foreach (JsonElement detail in details.EnumerateArray())
        {
            if (detail.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (detail.TryGetProperty("appIDs", out JsonElement plural) && plural.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement appId in plural.EnumerateArray())
                {
                    if (appId.ValueKind == JsonValueKind.String && appId.GetString() is { Length: > 0 } value)
                    {
                        appIds.Add(value);
                    }
                }
            }

            // The legacy single-app form is still accepted by Apple, so a domain using it is not broken.
            if (detail.TryGetProperty("appID", out JsonElement singular)
                && singular.ValueKind == JsonValueKind.String
                && singular.GetString() is { Length: > 0 } legacy)
            {
                appIds.Add(legacy);
            }
        }

        return appIds;
    }

    private static List<string> CollectFingerprints(JsonElement statements)
    {
        var fingerprints = new List<string>();

        foreach (JsonElement statement in statements.EnumerateArray())
        {
            if (statement.ValueKind != JsonValueKind.Object
                || !HasHandleAllUrls(statement)
                || !statement.TryGetProperty("target", out JsonElement target)
                || target.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!target.TryGetProperty("namespace", out JsonElement ns)
                || ns.ValueKind != JsonValueKind.String
                || !string.Equals(ns.GetString(), AndroidAppNamespace, StringComparison.Ordinal))
            {
                continue;
            }

            if (!target.TryGetProperty("sha256_cert_fingerprints", out JsonElement declared)
                || declared.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement fingerprint in declared.EnumerateArray())
            {
                if (fingerprint.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string normalized = NormalizeFingerprint(fingerprint.GetString());

                if (normalized.Length > 0 && !fingerprints.Contains(normalized, StringComparer.Ordinal))
                {
                    fingerprints.Add(normalized);
                }
            }
        }

        return fingerprints;
    }

    private static bool HasHandleAllUrls(JsonElement statement)
    {
        if (!statement.TryGetProperty("relation", out JsonElement relation) || relation.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement entry in relation.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String
                && string.Equals(entry.GetString(), HandleAllUrlsRelation, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsInvariant(List<string> values, string candidate)
    {
        foreach (string value in values)
        {
            if (string.Equals(value.Trim(), candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reduces a fingerprint to bare upper-case hex so that <c>14:6D:E9</c>, <c>14-6d-e9</c> and
    /// <c>146de9</c> compare equal.
    /// </summary>
    private static string NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length > MaxFingerprintLength)
        {
            return string.Empty;
        }

        return string.Create(
            CountHexDigits(fingerprint),
            fingerprint,
            static (destination, source) =>
            {
                int index = 0;

                foreach (char c in source)
                {
                    if (char.IsAsciiHexDigit(c))
                    {
                        destination[index++] = char.ToUpperInvariant(c);
                    }
                }
            });
    }

    private static int CountHexDigits(string value)
    {
        int count = 0;

        foreach (char c in value)
        {
            if (char.IsAsciiHexDigit(c))
            {
                count++;
            }
        }

        return count;
    }
}
