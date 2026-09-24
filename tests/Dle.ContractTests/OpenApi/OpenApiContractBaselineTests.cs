namespace Dle.ContractTests.OpenApi;

/// <summary>
/// The published-surface gate of §C.8: a breaking change to the control plane's contract fails the
/// build here rather than surprising an integrator in production.
/// </summary>
/// <remarks>
/// <para>
/// The committed file is <c>Contracts/openapi-v1.contract.json</c>. It is not the OpenAPI document —
/// it is the projection produced by <see cref="OpenApiContract"/>, which keeps the facts a caller
/// binds to and drops the prose. Regenerate it deliberately, never reflexively:
/// </para>
/// <code>
/// DLE_UPDATE_CONTRACT_BASELINE=1 dotnet run --project tests/Dle.ContractTests
/// </code>
/// <para>
/// and then read the resulting diff in the pull request. That diff is the changelog of the public
/// API; if it contains a removal, a rename or a new required field, the version has to move and the
/// SDKs have to be told.
/// </para>
/// </remarks>
[Collection(ControlApiTests.Name)]
public sealed class OpenApiContractBaselineTests
{
    /// <summary>Name of the committed baseline file.</summary>
    public const string BaselineFileName = "openapi-v1.contract.json";

    /// <summary>Environment variable that rewrites the baseline instead of asserting against it.</summary>
    public const string UpdateVariable = "DLE_UPDATE_CONTRACT_BASELINE";

    private readonly OpenApiDocumentFixture _api;

    public OpenApiContractBaselineTests(OpenApiDocumentFixture api) => _api = api;

    [Fact]
    public void PublishedSurface_ComparedToCommittedBaseline_HasNoBreakingChange()
    {
        string rendered = OpenApiContract.Render(_api.Contract);

        if (ShouldUpdateBaseline())
        {
            RepositoryLayout.WriteContract(BaselineFileName, rendered);

            Assert.Fail(
                UpdateVariable + " was set, so the baseline was rewritten rather than checked. " +
                "Review the diff of tests/Dle.ContractTests/Contracts/" + BaselineFileName +
                " and re-run without the variable.");
        }

        JsonObject baseline = OpenApiContract.Parse(ReadBaseline());

        IReadOnlyList<ContractChange> changes = OpenApiContractDiff.Compare(baseline, _api.Contract);

        List<ContractChange> breaking =
            [.. changes.Where(change => change.Kind == ContractChangeKind.Breaking)];

        Assert.True(
            breaking.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 The control plane's published contract changed in {breaking.Count} way(s) that break an
                 existing integrator. Each line names what a caller written against the committed
                 contract will now get wrong.

                 {OpenApiContractDiff.Describe(breaking)}

                 If the change is intended, bump the API version and regenerate the baseline with
                 {UpdateVariable}=1, so that the removal is visible in the pull request rather than in
                 an SDK's issue tracker.
                 """));
    }

    [Fact]
    public void PublishedSurface_AdditiveChangesOnly_DoNotFailTheGate()
    {
        JsonObject baseline = OpenApiContract.Parse(ReadBaseline());

        IReadOnlyList<ContractChange> changes = OpenApiContractDiff.Compare(baseline, _api.Contract);

        // Additive drift is allowed and is reported here purely so that a run that adds a route says
        // so out loud. The assertion is that additive changes are classified as additive: the gate
        // must not learn to cry wolf, because a gate that fires on a new optional field is a gate
        // whose baseline gets regenerated without anybody reading it.
        Assert.All(
            changes.Where(change => change.Kind == ContractChangeKind.Additive),
            change => Assert.NotEqual(ContractChangeKind.Breaking, change.Kind));
    }

    [Fact]
    public void PublishedDocument_IsOpenApi31()
    {
        // §B.7.3 names the version. It also decides how a nullable property is expressed — 3.1 uses a
        // type union, 3.0 used the `nullable` keyword — and every SDK generator branches on it.
        string version = _api.Document["openapi"]?.GetValue<string>() ?? string.Empty;

        Assert.StartsWith("3.1", version, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedDocument_DeclaresBothCredentialSchemes()
    {
        // §E.2.1 TB2: the SDK key and the control-plane key are different credentials reaching
        // different surfaces. Publishing one scheme would invite an integrator to assume one token
        // opens everything.
        JsonObject schemes = _api.Contract["securitySchemes"] as JsonObject ?? [];

        Assert.NotNull(schemes["ApiKey"]);
        Assert.NotNull(schemes["SdkKey"]);
    }

    private static bool ShouldUpdateBaseline() =>
        Environment.GetEnvironmentVariable(UpdateVariable) is "1" or "true";

    private static string ReadBaseline()
    {
        try
        {
            return RepositoryLayout.ReadContract(BaselineFileName);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException(
                "The committed contract baseline is missing. Create it once with " +
                UpdateVariable + "=1 and commit tests/Dle.ContractTests/Contracts/" + BaselineFileName + ".");
        }
    }
}
