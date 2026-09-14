using System.Reflection;

namespace Dle.ContractTests.OpenApi;

/// <summary>
/// The error half of the contract (§B.7.3: "Chyby: RFC 9457 Problem Details").
/// </summary>
/// <remarks>
/// <para>
/// An integrator branches on failure at least as often as on success, and it branches on two things:
/// the status code and the <c>type</c> member of the problem document. Both are public API.
/// <see cref="ProblemCodes"/> says so in its own remarks — "these strings are part of the public API
/// surface… a code may be added, but an existing one never changes meaning" — and a promise of that
/// kind is only worth something if the reference an integrator reads actually contains the list.
/// </para>
/// <para>
/// These tests are therefore about publication, not about behaviour: whether the surface an SDK
/// author is handed tells them what they can receive and what it will mean.
/// </para>
/// </remarks>
[Collection(ControlApiTests.Name)]
public sealed class ProblemContractTests
{
    /// <summary>Media type RFC 9457 assigns to a problem document.</summary>
    public const string ProblemMediaType = "application/problem+json";

    private readonly OpenApiDocumentFixture _api;

    public ProblemContractTests(OpenApiDocumentFixture api) => _api = api;

    /// <summary>Every problem type identifier the product declares, excluding the base URI.</summary>
    public static IReadOnlyList<string> DeclaredProblemCodes { get; } =
        [.. typeof(ProblemCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Where(field => !string.Equals(field.Name, nameof(ProblemCodes.Base), StringComparison.Ordinal))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void EveryProblemCode_IsDerivedFromThePublishedBaseUri()
    {
        // The base is what makes the identifiers dereferenceable as a family and greppable as a set.
        Assert.All(
            DeclaredProblemCodes,
            code => Assert.StartsWith(ProblemCodes.Base, code, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryProblemCode_IsDistinct()
    {
        Assert.Equal(DeclaredProblemCodes.Count, DeclaredProblemCodes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryOperationOfTheApi_DocumentsAtLeastOneProblemResponse()
    {
        // Scope: the authenticated API surface. `/.well-known/jwks.json` is deliberately excluded — it
        // is an anonymous, cacheable document with no caller-visible failure mode to describe.
        JsonObject operations = _api.Contract["operations"] as JsonObject ?? [];

        List<string> undocumented = [];

        foreach ((string key, JsonNode? node) in operations)
        {
            if (!IsApiOperation(key))
            {
                continue;
            }

            JsonObject responses = (node as JsonObject)?["responses"] as JsonObject ?? [];

            bool documented = responses.Any(response =>
                (response.Value?["content"] as JsonObject)?[ProblemMediaType] is not null);

            if (!documented)
            {
                undocumented.Add(key);
            }
        }

        Assert.True(
            undocumented.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 {undocumented.Count} operation(s) publish no RFC 9457 failure at all, so an integrator
                 reading the reference cannot tell what an error looks like or when it happens. Every
                 one of these sits behind a credential and can therefore answer 401 at minimum.

                 {string.Join(Environment.NewLine, undocumented.Select(operation => "  " + operation))}
                 """));
    }

    [Fact]
    public void EveryDocumentedFailure_UsesTheProblemMediaType()
    {
        // A 4xx or 5xx that answers application/json rather than application/problem+json is the
        // second error format §B.7.3 exists to prevent.
        JsonObject operations = _api.Contract["operations"] as JsonObject ?? [];

        List<string> wrong = [];

        foreach ((string key, JsonNode? node) in operations)
        {
            JsonObject responses = (node as JsonObject)?["responses"] as JsonObject ?? [];

            foreach ((string status, JsonNode? response) in responses)
            {
                if (!int.TryParse(status, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)
                    || code < 400)
                {
                    continue;
                }

                JsonObject content = (response as JsonObject)?["content"] as JsonObject ?? [];

                if (content.Count > 0 && content[ProblemMediaType] is null)
                {
                    wrong.Add(string.Create(CultureInfo.InvariantCulture, $"{key} response {status}"));
                }
            }
        }

        Assert.True(
            wrong.Count == 0,
            "Failure responses must be RFC 9457 problem documents: " + string.Join(", ", wrong));
    }

    [Fact]
    public void ProblemDetailsSchema_CarriesTheRfc9457Members()
    {
        JsonObject schemas = _api.Contract["schemas"] as JsonObject ?? [];
        JsonObject properties = schemas["ProblemDetails"]?["properties"] as JsonObject ?? [];

        Assert.All(
            new[] { "type", "title", "status", "detail", "instance" },
            member => Assert.True(
                properties[member] is not null,
                "The published ProblemDetails schema is missing the RFC 9457 member '" + member + "'."));
    }

    [Fact]
    public void EveryProblemCodeUsedInTheCode_AppearsInThePublishedDocument()
    {
        // The strongest form of the contract: an integrator can find every identifier they may have to
        // branch on without reading the server's source. Searching the whole document rather than a
        // particular member on purpose — publishing them as an enumeration on the `type` property, as
        // an example, or as a list in a description are all acceptable answers to the requirement, and
        // fixing this should not be constrained to one of them.
        string document = _api.Document.ToJsonString();

        List<string> missing =
            [.. DeclaredProblemCodes.Where(code => !document.Contains(code, StringComparison.Ordinal))];

        Assert.True(
            missing.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 {missing.Count} of the {DeclaredProblemCodes.Count} problem type identifiers the
                 product declares appear nowhere in the OpenAPI document. ProblemCodes calls them part
                 of the public API surface, and §B.7.3 makes the document the published reference, so
                 an integrator has no way to learn them except by reading the server's source or by
                 triggering each failure in production.

                 {string.Join(Environment.NewLine, missing.Select(code => "  " + code))}

                 Fix in the product, not here: enumerate them on the ProblemDetails `type` property in
                 an OpenAPI document transformer, or list them in the document description.
                 """));
    }

    private static bool IsApiOperation(string operationKey)
    {
        int space = operationKey.IndexOf(' ', StringComparison.Ordinal);
        string path = space < 0 ? operationKey : operationKey[(space + 1)..];

        return path.StartsWith("/api/v1/", StringComparison.Ordinal)
            || path.StartsWith("/v1/", StringComparison.Ordinal);
    }
}
