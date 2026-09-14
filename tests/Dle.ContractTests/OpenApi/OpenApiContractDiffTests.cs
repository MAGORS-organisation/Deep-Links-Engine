namespace Dle.ContractTests.OpenApi;

/// <summary>
/// Tests of the differ itself, against synthetic documents.
/// </summary>
/// <remarks>
/// A contract gate is only worth the build time if it fires on the changes it claims to catch and
/// stays quiet on the ones it claims to allow. These are the cases the gate promises, written as a
/// pair each time — the breaking direction and the additive one — because a differ that reports
/// everything is exactly as useless as a differ that reports nothing.
/// </remarks>
public sealed class OpenApiContractDiffTests
{
    private const string BaseDocument =
        """
        {
          "openapi": "3.1.1",
          "paths": {
            "/api/v1/links": {
              "get": {
                "operationId": "ListLinks",
                "parameters": [
                  { "name": "limit", "in": "query", "schema": { "type": "integer", "format": "int32" } }
                ],
                "responses": {
                  "200": {
                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/LinkResponse" } } }
                  }
                }
              },
              "post": {
                "operationId": "CreateLink",
                "requestBody": {
                  "required": true,
                  "content": { "application/json": { "schema": { "$ref": "#/components/schemas/CreateLinkRequest" } } }
                },
                "responses": {
                  "201": {
                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/LinkResponse" } } }
                  }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "CreateLinkRequest": {
                "type": "object",
                "required": [ "target_url" ],
                "properties": {
                  "target_url": { "type": "string" },
                  "slug": { "type": [ "null", "string" ] },
                  "mode": { "type": "string", "enum": [ "auto", "always", "never" ] }
                }
              },
              "LinkResponse": {
                "type": "object",
                "required": [ "id" ],
                "properties": {
                  "id": { "type": "string" },
                  "short_url": { "type": "string" }
                }
              }
            },
            "securitySchemes": {
              "ApiKey": { "type": "http", "scheme": "bearer" }
            }
          }
        }
        """;

    [Fact]
    public void Compare_IdenticalDocuments_ReportsNothing()
    {
        IReadOnlyList<ContractChange> changes = Diff(BaseDocument, BaseDocument);

        Assert.Empty(changes);
    }

    [Fact]
    public void Compare_RemovedPath_IsBreakingAndNamesTheOperation()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document => ((JsonObject)document["paths"]!).Remove("/api/v1/links")));

        ContractChange change = Assert.Single(
            changes,
            c => c.Kind == ContractChangeKind.Breaking && c.Location == "GET /api/v1/links");

        Assert.Contains("removed", change.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_RemovedOperationOnKeptPath_IsBreaking()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document => ((JsonObject)document["paths"]!["/api/v1/links"]!).Remove("post")));

        Assert.Contains(
            changes,
            change => change.Kind == ContractChangeKind.Breaking && change.Location == "POST /api/v1/links");

        Assert.DoesNotContain(
            changes,
            change => change.Kind == ContractChangeKind.Breaking && change.Location == "GET /api/v1/links");
    }

    [Fact]
    public void Compare_RemovedProperty_IsBreakingAndNamesTheProperty()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document =>
                ((JsonObject)document["components"]!["schemas"]!["LinkResponse"]!["properties"]!).Remove("short_url")));

        ContractChange change = Assert.Single(changes, c => c.Kind == ContractChangeKind.Breaking);

        Assert.Equal("schema LinkResponse.short_url", change.Location);
    }

    [Fact]
    public void Compare_RenamedProperty_IsBreakingBecauseItIsARemovalAndAnAddition()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document =>
            {
                JsonObject properties = (JsonObject)document["components"]!["schemas"]!["LinkResponse"]!["properties"]!;
                properties.Remove("short_url");
                properties["shortUrl"] = new JsonObject { ["type"] = "string" };
            }));

        Assert.Contains(
            changes,
            change => change.Kind == ContractChangeKind.Breaking && change.Location == "schema LinkResponse.short_url");
    }

    [Fact]
    public void Compare_NarrowedType_IsBreaking()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document =>
                ((JsonObject)document["components"]!["schemas"]!["CreateLinkRequest"]!["properties"]!)["slug"] =
                    new JsonObject { ["type"] = "string" }));

        ContractChange change = Assert.Single(changes, c => c.Kind == ContractChangeKind.Breaking);

        Assert.Equal("schema CreateLinkRequest.slug", change.Location);
        Assert.Contains("no longer admits null", change.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_WidenedType_IsAdditive()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document =>
                ((JsonObject)document["components"]!["schemas"]!["LinkResponse"]!["properties"]!)["id"] =
                    new JsonObject { ["type"] = new JsonArray("null", "string") }));

        Assert.DoesNotContain(changes, change => change.Kind == ContractChangeKind.Breaking);
        Assert.Contains(changes, change => change.Kind == ContractChangeKind.Additive);
    }

    [Fact]
    public void Compare_NewRequiredProperty_IsBreaking()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document =>
            {
                JsonObject schema = (JsonObject)document["components"]!["schemas"]!["CreateLinkRequest"]!;
                ((JsonObject)schema["properties"]!)["domain_id"] = new JsonObject { ["type"] = "string" };
                ((JsonArray)schema["required"]!).Add("domain_id");
            }));

        Assert.Contains(
            changes,
            change => change.Kind == ContractChangeKind.Breaking
                && change.Location == "schema CreateLinkRequest"
                && change.Message.Contains("'domain_id' became required", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_NewOptionalProperty_IsAdditive()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document =>
                ((JsonObject)document["components"]!["schemas"]!["CreateLinkRequest"]!["properties"]!)["note"] =
                    new JsonObject { ["type"] = new JsonArray("null", "string") }));

        Assert.DoesNotContain(changes, change => change.Kind == ContractChangeKind.Breaking);
        Assert.Contains(changes, change => change.Location == "schema CreateLinkRequest.note");
    }

    [Fact]
    public void Compare_NewPath_IsAdditive()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document => ((JsonObject)document["paths"]!)["/api/v1/campaigns"] = new JsonObject
            {
                ["get"] = new JsonObject { ["operationId"] = "ListCampaigns", ["responses"] = new JsonObject() },
            }));

        Assert.DoesNotContain(changes, change => change.Kind == ContractChangeKind.Breaking);
        Assert.Contains(changes, change => change.Location == "GET /api/v1/campaigns");
    }

    [Fact]
    public void Compare_NewRequiredQueryParameter_IsBreaking()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document => ((JsonArray)document["paths"]!["/api/v1/links"]!["get"]!["parameters"]!).Add(
                new JsonObject
                {
                    ["name"] = "tenant_id",
                    ["in"] = "query",
                    ["required"] = true,
                    ["schema"] = new JsonObject { ["type"] = "string" },
                })));

        Assert.Contains(
            changes,
            change => change.Kind == ContractChangeKind.Breaking
                && change.Location == "GET /api/v1/links parameter tenant_id@query");
    }

    [Fact]
    public void Compare_OptionalParameterBecomingRequired_IsBreaking()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document =>
                ((JsonObject)document["paths"]!["/api/v1/links"]!["get"]!["parameters"]![0]!)["required"] = true));

        Assert.Contains(
            changes,
            change => change.Kind == ContractChangeKind.Breaking
                && change.Location == "GET /api/v1/links parameter limit@query");
    }

    [Fact]
    public void Compare_RemovedResponseStatus_IsBreaking()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document => ((JsonObject)document["paths"]!["/api/v1/links"]!["post"]!["responses"]!).Remove("201")));

        Assert.Contains(
            changes,
            change => change.Kind == ContractChangeKind.Breaking
                && change.Location == "POST /api/v1/links response 201");
    }

    [Fact]
    public void Compare_RemovedEnumerationMember_IsBreaking()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document =>
                ((JsonObject)document["components"]!["schemas"]!["CreateLinkRequest"]!["properties"]!)["mode"] =
                    new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("auto", "always"),
                    }));

        Assert.Contains(
            changes,
            change => change.Kind == ContractChangeKind.Breaking
                && change.Message.Contains("no longer contains", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_ChangedOperationId_IsBreaking()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document => ((JsonObject)document["paths"]!["/api/v1/links"]!["post"]!)["operationId"] = "AddLink"));

        Assert.Contains(
            changes,
            change => change.Kind == ContractChangeKind.Breaking
                && change.Message.Contains("operationId", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_RemovedSecurityScheme_IsBreaking()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document => ((JsonObject)document["components"]!["securitySchemes"]!).Remove("ApiKey")));

        Assert.Contains(
            changes,
            change => change.Kind == ContractChangeKind.Breaking && change.Location == "securityScheme ApiKey");
    }

    [Fact]
    public void Compare_ChangedSchemaReference_IsBreaking()
    {
        IReadOnlyList<ContractChange> changes = Diff(
            BaseDocument,
            Mutate(document =>
                ((JsonObject)document["paths"]!["/api/v1/links"]!["post"]!["responses"]!["201"]!["content"]!
                    ["application/json"]!)["schema"] = new JsonObject
                    {
                        ["$ref"] = "#/components/schemas/CreateLinkRequest",
                    }));

        Assert.Contains(
            changes,
            change => change.Kind == ContractChangeKind.Breaking
                && change.Message.Contains("referenced schema changed", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_IsStableAcrossMemberOrder()
    {
        // The projection sorts everything it emits, so a reordered document renders identically and a
        // baseline never churns because a generator changed its member order.
        JsonObject original = OpenApiContract.Project(Parse(BaseDocument));

        JsonObject reordered = OpenApiContract.Project(Mutate(document =>
        {
            JsonObject schemas = (JsonObject)document["components"]!["schemas"]!;
            JsonNode response = schemas["LinkResponse"]!.DeepClone();
            schemas.Remove("LinkResponse");
            schemas["LinkResponse"] = response;
        }));

        Assert.Equal(OpenApiContract.Render(original), OpenApiContract.Render(reordered));
    }

    private static IReadOnlyList<ContractChange> Diff(string baseline, string current) =>
        OpenApiContractDiff.Compare(
            OpenApiContract.Project(Parse(baseline)),
            OpenApiContract.Project(Parse(current)));

    private static IReadOnlyList<ContractChange> Diff(string baseline, JsonObject current) =>
        OpenApiContractDiff.Compare(OpenApiContract.Project(Parse(baseline)), OpenApiContract.Project(current));

    private static JsonObject Parse(string json) =>
        JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("Not a JSON object.");

    private static JsonObject Mutate(Action<JsonObject> mutation)
    {
        JsonObject document = Parse(BaseDocument);
        mutation(document);

        return document;
    }
}
