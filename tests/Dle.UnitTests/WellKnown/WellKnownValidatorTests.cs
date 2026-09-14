using Dle.Domain.WellKnown;
using Xunit;

namespace Dle.UnitTests.WellKnown;

/// <summary>
/// FR-143, FR-144, TC-123, TC-124. Neither Apple nor Google follows a redirect to a well-known
/// file, so a single hop is fatal, not cosmetic. The upload-certificate case is the opposite: it is
/// a warning, because the file may still be correct and the operator has to be told, not blocked.
/// </summary>
public sealed class WellKnownValidatorTests
{
    private const string AppId = "ABCDE12345.sk.zakaznik.app";
    private const string Fingerprint =
        "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";
    private const string OtherFingerprint =
        "11:22:33:44:55:66:77:88:99:00:AA:BB:CC:DD:EE:FF:11:22:33:44:55:66:77:88:99:00:AA:BB:CC:DD:EE:FF";

    private const string ValidAasa =
        """{"applinks":{"details":[{"appIDs":["ABCDE12345.sk.zakaznik.app"],"components":[{"/":"/*"}]}]}}""";

    private const string ValidAssetLinks =
        """[{"relation":["delegate_permission/common.handle_all_urls"],"target":{"namespace":"android_app","package_name":"sk.zakaznik.app","sha256_cert_fingerprints":["""
        + "\"" + Fingerprint + "\"" + "]}}]";

    // ================================================================ TC-124: redirects are fatal

    [Theory]
    [Trait("TestCase", "TC-124")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void ValidateAasa_AnyRedirect_IsAFatalError(int redirectCount)
    {
        IReadOnlyList<WellKnownValidationIssue> issues =
            WellKnownValidator.ValidateAasa(ValidAasa, "application/json", 200, redirectCount, [AppId]);

        WellKnownValidationIssue issue = Assert.Single(issues, i => i.Code == WellKnownValidator.ErrRedirect);
        Assert.True(issue.IsError);
        Assert.Contains("redirect", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("TestCase", "TC-124")]
    public void ValidateAssetLinks_AnyRedirect_IsAFatalError()
    {
        IReadOnlyList<WellKnownValidationIssue> issues =
            WellKnownValidator.ValidateAssetLinks(ValidAssetLinks, "application/json", 200, 1, [Fingerprint]);

        Assert.True(Assert.Single(issues, i => i.Code == WellKnownValidator.ErrRedirect).IsError);
    }

    [Fact]
    [Trait("TestCase", "TC-124")]
    public void ValidateAasa_HttpToHttpsRedirect_StopsBeforeParsingTheBody()
    {
        // The redirect target may well contain a perfect file; it is still not the file Apple reads.
        IReadOnlyList<WellKnownValidationIssue> issues =
            WellKnownValidator.ValidateAasa(ValidAasa, "application/json", 200, 1, ["ZZZZZ99999.other.app"]);

        Assert.DoesNotContain(issues, i => i.Code == WellKnownValidator.ErrAppIdMismatch);
        Assert.Contains(issues, i => i.Code == WellKnownValidator.ErrRedirect);
    }

    // ================================================================ transport

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(0)]
    public void ValidateAasa_StatusOtherThanTwoHundred_IsAFatalError(int statusCode)
    {
        IReadOnlyList<WellKnownValidationIssue> issues =
            WellKnownValidator.ValidateAasa(ValidAasa, "application/json", statusCode, 0, [AppId]);

        Assert.True(Assert.Single(issues, i => i.Code == WellKnownValidator.ErrStatus).IsError);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/plain")]
    [InlineData("application/pkcs7-mime")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateAasa_WrongContentType_IsAFatalError(string? contentType)
    {
        IReadOnlyList<WellKnownValidationIssue> issues =
            WellKnownValidator.ValidateAasa(ValidAasa, contentType, 200, 0, [AppId]);

        Assert.True(Assert.Single(issues, i => i.Code == WellKnownValidator.ErrContentType).IsError);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("APPLICATION/JSON")]
    [InlineData("  application/json  ")]
    public void ValidateAasa_AcceptableContentType_RaisesNoIssue(string contentType)
    {
        Assert.Empty(WellKnownValidator.ValidateAasa(ValidAasa, contentType, 200, 0, [AppId]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateAasa_EmptyBody_IsAFatalError(string? body)
    {
        IReadOnlyList<WellKnownValidationIssue> issues =
            WellKnownValidator.ValidateAasa(body, "application/json", 200, 0, [AppId]);

        Assert.Contains(issues, i => i.Code == WellKnownValidator.ErrMalformed && i.IsError);
    }

    // ================================================================ malformed documents

    [Theory]
    [InlineData("{ not json")]
    [InlineData("<html><body>404</body></html>")]
    [InlineData("[1,2,3")]
    public void ValidateAasa_MalformedJson_IsAFatalError(string body)
    {
        IReadOnlyList<WellKnownValidationIssue> issues =
            WellKnownValidator.ValidateAasa(body, "application/json", 200, 0, [AppId]);

        Assert.True(Assert.Single(issues, i => i.Code == WellKnownValidator.ErrMalformed).IsError);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"applinks":{}}""")]
    [InlineData("""{"applinks":{"details":{}}}""")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    public void ValidateAasa_ValidJsonWithTheWrongShape_IsAFatalError(string body)
    {
        IReadOnlyList<WellKnownValidationIssue> issues =
            WellKnownValidator.ValidateAasa(body, "application/json", 200, 0, [AppId]);

        Assert.True(Assert.Single(issues, i => i.Code == WellKnownValidator.ErrMalformed).IsError);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"statements":[]}""")]
    [InlineData("[]")]
    [InlineData("""[{"relation":["delegate_permission/common.get_login_creds"],"target":{"namespace":"android_app","package_name":"sk.zakaznik.app","sha256_cert_fingerprints":["AABB"]}}]""")]
    [InlineData("""[{"relation":["delegate_permission/common.handle_all_urls"],"target":{"namespace":"web","site":"https://example.com"}}]""")]
    public void ValidateAssetLinks_DocumentWithoutAUsableStatement_IsAFatalError(string body)
    {
        IReadOnlyList<WellKnownValidationIssue> issues =
            WellKnownValidator.ValidateAssetLinks(body, "application/json", 200, 0, [Fingerprint]);

        Assert.Contains(issues, i => i.Code == WellKnownValidator.ErrMalformed && i.IsError);
    }

    // ================================================================ identity mismatch

    [Fact]
    public void ValidateAasa_ExpectedAppIdMissing_IsAFatalError()
    {
        IReadOnlyList<WellKnownValidationIssue> issues =
            WellKnownValidator.ValidateAasa(ValidAasa, "application/json", 200, 0, ["ZZZZZ99999.other.app"]);

        WellKnownValidationIssue issue = Assert.Single(issues, i => i.Code == WellKnownValidator.ErrAppIdMismatch);
        Assert.True(issue.IsError);
        Assert.Contains("ZZZZZ99999.other.app", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAasa_ExpectedAppIdPresent_RaisesNoIssue()
    {
        Assert.Empty(WellKnownValidator.ValidateAasa(ValidAasa, "application/json", 200, 0, [AppId]));
    }

    [Fact]
    public void ValidateAasa_LegacySingleAppIdKey_IsStillAccepted()
    {
        const string Legacy = """{"applinks":{"details":[{"appID":"ABCDE12345.sk.zakaznik.app","paths":["*"]}]}}""";

        Assert.Empty(WellKnownValidator.ValidateAasa(Legacy, "application/json", 200, 0, [AppId]));
    }

    [Fact]
    public void ValidateAasa_NoExpectedAppIds_OnlyChecksTheShape()
    {
        Assert.Empty(WellKnownValidator.ValidateAasa(ValidAasa, "application/json", 200, 0, []));
    }

    [Fact]
    public void ValidateAssetLinks_ExpectedFingerprintMissing_IsAFatalError()
    {
        IReadOnlyList<WellKnownValidationIssue> issues =
            WellKnownValidator.ValidateAssetLinks(ValidAssetLinks, "application/json", 200, 0, [OtherFingerprint]);

        Assert.True(Assert.Single(issues, i => i.Code == WellKnownValidator.ErrAppIdMismatch).IsError);
    }

    [Fact]
    public void ValidateAssetLinks_FingerprintSpelledDifferently_StillMatches()
    {
        string lowercaseNoSeparators = Fingerprint.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        Assert.Empty(WellKnownValidator.ValidateAssetLinks(
            ValidAssetLinks, "application/json", 200, 0, [lowercaseNoSeparators]));
    }

    // ================================================================ TC-123: upload certificate

    [Fact]
    [Trait("TestCase", "TC-123")]
    public void ValidateAssetLinks_FingerprintOutsideANonEmptyPlaySigningList_IsAWarningNotAnError()
    {
        IReadOnlyList<WellKnownValidationIssue> issues = WellKnownValidator.ValidateAssetLinks(
            ValidAssetLinks,
            "application/json",
            200,
            0,
            expectedFingerprints: [Fingerprint],
            knownPlayFingerprints: [OtherFingerprint]);

        WellKnownValidationIssue issue = Assert.Single(issues, i => i.Code == WellKnownValidator.WarnUploadCertificate);
        Assert.False(issue.IsError);
        Assert.Contains("upload certificate", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(issues, i => i.IsError);
    }

    [Fact]
    [Trait("TestCase", "TC-123")]
    public void ValidateAssetLinks_FingerprintInsideThePlaySigningList_IsSilent()
    {
        Assert.Empty(WellKnownValidator.ValidateAssetLinks(
            ValidAssetLinks,
            "application/json",
            200,
            0,
            expectedFingerprints: [Fingerprint],
            knownPlayFingerprints: [OtherFingerprint, Fingerprint]));
    }

    [Fact]
    [Trait("TestCase", "TC-123")]
    public void ValidateAssetLinks_WithoutAPlaySigningList_CannotJudgeAndStaysSilent()
    {
        // The five argument overload is the "we do not know the Play certificates" case. Warning
        // there would cry wolf on every self-hosted installation without Play API credentials.
        Assert.Empty(WellKnownValidator.ValidateAssetLinks(ValidAssetLinks, "application/json", 200, 0, [Fingerprint]));
    }

    [Fact]
    [Trait("TestCase", "TC-123")]
    public void LooksLikeUploadCertificate_EmptyOrNullKnownList_IsFalse()
    {
        Assert.False(WellKnownValidator.LooksLikeUploadCertificate(Fingerprint, []));
        Assert.False(WellKnownValidator.LooksLikeUploadCertificate(Fingerprint, null!));
    }

    [Fact]
    [Trait("TestCase", "TC-123")]
    public void LooksLikeUploadCertificate_ComparesRegardlessOfSpelling()
    {
        string lowercaseNoSeparators = Fingerprint.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        Assert.False(WellKnownValidator.LooksLikeUploadCertificate(Fingerprint, [lowercaseNoSeparators]));
        Assert.True(WellKnownValidator.LooksLikeUploadCertificate(Fingerprint, [OtherFingerprint]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("zzzz:wxyz")]
    public void LooksLikeUploadCertificate_NothingHexadecimalToCompare_IsFalse(string fingerprint)
    {
        // Nothing to compare means nothing to warn about; the malformed-file error covers that case.
        Assert.False(WellKnownValidator.LooksLikeUploadCertificate(fingerprint, [Fingerprint]));
    }

    [Fact]
    public void LooksLikeUploadCertificate_OverLongInput_IsFalse()
    {
        Assert.False(WellKnownValidator.LooksLikeUploadCertificate(new string('A', 129), [Fingerprint]));
    }

    // ================================================================ codes

    [Fact]
    public void IssueCodes_AreTheDocumentedStableStrings()
    {
        Assert.Equal("well_known.redirect", WellKnownValidator.ErrRedirect);
        Assert.Equal("well_known.content_type", WellKnownValidator.ErrContentType);
        Assert.Equal("well_known.status", WellKnownValidator.ErrStatus);
        Assert.Equal("well_known.malformed", WellKnownValidator.ErrMalformed);
        Assert.Equal("well_known.appid_mismatch", WellKnownValidator.ErrAppIdMismatch);
        Assert.Equal("well_known.upload_certificate", WellKnownValidator.WarnUploadCertificate);
    }

    [Fact]
    public void ValidateAasa_HealthyFile_ProducesNoIssuesAtAll()
    {
        Assert.Empty(WellKnownValidator.ValidateAasa(ValidAasa, "application/json", 200, 0, [AppId]));
    }
}
