using System.Net;

namespace Dle.ContractTests.Infrastructure;

/// <summary>
/// Boots the control plane once for the whole assembly and holds the document it publishes.
/// </summary>
/// <remarks>
/// The document is read from the running host at <c>/openapi/v1.json</c> rather than from the file the
/// build can emit. They are the same document, but only one of them is what an integrator actually
/// fetches, and a gate that guards a build artefact instead of the served surface is a gate that can
/// pass while the deployed API has already changed.
/// </remarks>
public sealed class OpenApiDocumentFixture : IAsyncLifetime
{
    private ControlApiFactory? _factory;

    /// <summary>Path the document is served from.</summary>
    public const string DocumentPath = "/openapi/v1.json";

    /// <summary>The document as served, unmodified.</summary>
    public JsonObject Document { get; private set; } = [];

    /// <summary>The contract-bearing projection of <see cref="Document"/>.</summary>
    public JsonObject Contract { get; private set; } = [];

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _factory = new ControlApiFactory();

        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(DocumentPath, UriKind.Relative),
            TestContext.Current.CancellationToken);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                "The control plane did not serve its OpenAPI document; it answered " +
                ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
        }

        string json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Document = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("The published OpenAPI document is not a JSON object.");

        Contract = OpenApi.OpenApiContract.Project(Document);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _factory?.Dispose();
        _factory = null;

        return ValueTask.CompletedTask;
    }
}

/// <summary>Marks the tests that share the booted control plane.</summary>
[CollectionDefinition(Name)]
public sealed class ControlApiTests : ICollectionFixture<OpenApiDocumentFixture>
{
    /// <summary>Name of the collection.</summary>
    public const string Name = "control-api";
}
