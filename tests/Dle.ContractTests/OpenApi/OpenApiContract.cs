namespace Dle.ContractTests.OpenApi;

/// <summary>
/// Reduces a generated OpenAPI document to the facts an integrator's code actually depends on.
/// </summary>
/// <remarks>
/// <para>
/// The baseline this project commits is a projection, not a copy of the document. That is a
/// deliberate choice and it is what makes the gate usable: the raw document carries the XML
/// documentation of every handler, so rewording a sentence in a summary would rewrite the baseline
/// and bury the one line that mattered. What an SDK breaks on is narrower — whether the route still
/// exists, whether the property is still called what it was called, whether the type still admits the
/// value it used to, whether a field it never sent has become required.
/// </para>
/// <para>
/// Everything projected here is therefore load bearing, and everything omitted — descriptions,
/// summaries, tags, examples, server list, document version — is prose. Omitting prose is not a
/// loosening of the gate; a change that only touches prose cannot break a caller, and a gate that
/// fires on it teaches people to regenerate the baseline without reading the diff.
/// </para>
/// </remarks>
public static class OpenApiContract
{
    /// <summary>HTTP methods an OpenAPI path item may carry an operation for.</summary>
    private static readonly string[] OperationKeys =
        ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    /// <summary>Schema members that carry contract meaning and are copied verbatim.</summary>
    private static readonly string[] ScalarSchemaKeys =
        [
            "type", "format", "$ref", "const", "pattern", "minimum", "maximum",
            "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength",
            "minItems", "maxItems", "uniqueItems", "multipleOf", "additionalProperties",
        ];

    /// <summary>
    /// Projects a generated OpenAPI document onto its contract-bearing members.
    /// </summary>
    /// <param name="document">The document, as served from <c>/openapi/v1.json</c>.</param>
    /// <returns>A canonical object: stable member order, no prose, no formatting noise.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static JsonObject Project(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        JsonObject result = new()
        {
            ["openapi"] = document["openapi"]?.GetValue<string>() ?? string.Empty,
            ["operations"] = ProjectOperations(document["paths"] as JsonObject),
            ["schemas"] = ProjectNamed(document["components"]?["schemas"] as JsonObject, ProjectSchema),
            ["securitySchemes"] = ProjectNamed(
                document["components"]?["securitySchemes"] as JsonObject,
                ProjectSecurityScheme),
        };

        return result;
    }

    /// <summary>Renders a projection as the exact text the baseline file holds.</summary>
    /// <param name="contract">A projection produced by <see cref="Project(JsonObject)"/>.</param>
    /// <returns>Indented JSON terminated by a single newline, with Unix line endings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Line endings are normalized because the baseline is committed and this repository is developed
    /// on both Windows and Linux. A gate that fails on a checkout's line-ending policy is a gate
    /// people switch off.
    /// </remarks>
    public static string Render(JsonObject contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        string json = contract.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        return json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>Parses a rendered baseline back into a projection.</summary>
    /// <param name="json">The baseline text.</param>
    /// <returns>The projection.</returns>
    /// <exception cref="InvalidOperationException">The text is not a JSON object.</exception>
    public static JsonObject Parse(string json) =>
        JsonNode.Parse(json) as JsonObject
        ?? throw new InvalidOperationException("The contract baseline is not a JSON object.");

    /// <summary>
    /// Flattens the path item map into one entry per operation, keyed <c>METHOD path</c>.
    /// </summary>
    /// <remarks>
    /// One entry per operation rather than a nested map because that is the unit a caller binds to:
    /// removing <c>DELETE /api/v1/links/{id}</c> while keeping <c>GET</c> on the same path is a
    /// breaking change, and a nested comparison would have to reconstruct that distinction anyway.
    /// </remarks>
    private static JsonObject ProjectOperations(JsonObject? paths)
    {
        JsonObject operations = new();

        if (paths is null)
        {
            return operations;
        }

        foreach (string path in paths.Select(entry => entry.Key).Order(StringComparer.Ordinal))
        {
            if (paths[path] is not JsonObject item)
            {
                continue;
            }

            foreach (string method in OperationKeys)
            {
                if (item[method] is not JsonObject operation)
                {
                    continue;
                }

                string key = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{method.ToUpperInvariant()} {path}");

                operations[key] = ProjectOperation(operation);
            }
        }

        return operations;
    }

    /// <summary>Projects one operation onto its parameters, body and responses.</summary>
    private static JsonObject ProjectOperation(JsonObject operation)
    {
        JsonObject projected = new()
        {
            ["operationId"] = operation["operationId"]?.GetValue<string>() ?? string.Empty,
            ["parameters"] = ProjectParameters(operation["parameters"] as JsonArray),
            ["requestBody"] = ProjectRequestBody(operation["requestBody"] as JsonObject),
            ["responses"] = ProjectResponses(operation["responses"] as JsonObject),
        };

        return projected;
    }

    /// <summary>Projects the parameter list, keyed by name and location.</summary>
    /// <remarks>
    /// Keyed rather than ordered: reordering query parameters cannot break a caller, renaming one
    /// always does, and moving a parameter from the query to the path does too. The key carries both
    /// facts and the order carries neither.
    /// </remarks>
    private static JsonObject ProjectParameters(JsonArray? parameters)
    {
        JsonObject projected = new();

        if (parameters is null)
        {
            return projected;
        }

        List<(string Key, JsonObject Value)> entries = [];

        foreach (JsonNode? node in parameters)
        {
            if (node is not JsonObject parameter)
            {
                continue;
            }

            string name = parameter["name"]?.GetValue<string>() ?? string.Empty;
            string location = parameter["in"]?.GetValue<string>() ?? string.Empty;

            JsonObject value = new()
            {
                ["required"] = parameter["required"]?.GetValue<bool>() ?? false,
                ["schema"] = ProjectSchema(parameter["schema"]),
            };

            entries.Add((string.Create(CultureInfo.InvariantCulture, $"{name}@{location}"), value));
        }

        foreach ((string key, JsonObject value) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            projected[key] = value;
        }

        return projected;
    }

    /// <summary>Projects the request body onto whether it is required and what it accepts.</summary>
    private static JsonObject? ProjectRequestBody(JsonObject? body)
    {
        if (body is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["required"] = body["required"]?.GetValue<bool>() ?? false,
            ["content"] = ProjectContent(body["content"] as JsonObject),
        };
    }

    /// <summary>Projects the response map onto status codes and their media types.</summary>
    private static JsonObject ProjectResponses(JsonObject? responses)
    {
        JsonObject projected = new();

        if (responses is null)
        {
            return projected;
        }

        foreach (string status in responses.Select(entry => entry.Key).Order(StringComparer.Ordinal))
        {
            projected[status] = new JsonObject
            {
                ["content"] = ProjectContent(responses[status]?["content"] as JsonObject),
            };
        }

        return projected;
    }

    /// <summary>Projects a content map onto media type to schema.</summary>
    private static JsonObject ProjectContent(JsonObject? content)
    {
        JsonObject projected = new();

        if (content is null)
        {
            return projected;
        }

        foreach (string mediaType in content.Select(entry => entry.Key).Order(StringComparer.Ordinal))
        {
            projected[mediaType] = ProjectSchema(content[mediaType]?["schema"]);
        }

        return projected;
    }

    /// <summary>Projects a named map through a projector, in a stable order.</summary>
    private static JsonObject ProjectNamed(JsonObject? source, Func<JsonNode?, JsonNode?> project)
    {
        JsonObject projected = new();

        if (source is null)
        {
            return projected;
        }

        foreach (string name in source.Select(entry => entry.Key).Order(StringComparer.Ordinal))
        {
            projected[name] = project(source[name]);
        }

        return projected;
    }

    /// <summary>Projects a security scheme onto how a credential is presented.</summary>
    private static JsonNode? ProjectSecurityScheme(JsonNode? scheme)
    {
        if (scheme is not JsonObject value)
        {
            return null;
        }

        JsonObject projected = new();

        foreach (string key in new[] { "type", "scheme", "name", "in", "bearerFormat", "openIdConnectUrl" })
        {
            if (value[key] is { } member)
            {
                projected[key] = member.DeepClone();
            }
        }

        return projected;
    }

    /// <summary>
    /// Projects a schema onto the members that constrain a value.
    /// </summary>
    /// <remarks>
    /// <c>type</c> is normalized to a sorted array even when the document writes a bare string, so
    /// that <c>"string"</c> and <c>["string"]</c> compare equal and <c>["null","string"]</c> narrowing
    /// to <c>"string"</c> is visible as a set difference rather than as a change of JSON kind.
    /// </remarks>
    private static JsonNode? ProjectSchema(JsonNode? schema)
    {
        if (schema is not JsonObject value)
        {
            return schema?.DeepClone();
        }

        JsonObject projected = [];

        if (value["type"] is { } type)
        {
            projected["type"] = NormalizeTypes(type);
        }

        foreach (string key in ScalarSchemaKeys)
        {
            if (key is "type")
            {
                continue;
            }

            if (value[key] is { } member)
            {
                projected[key] = member is JsonObject nested ? ProjectSchema(nested) : member.DeepClone();
            }
        }

        if (value["required"] is JsonArray required)
        {
            JsonArray sorted = [];

            foreach (string name in required
                .Select(node => node?.GetValue<string>() ?? string.Empty)
                .Order(StringComparer.Ordinal))
            {
                sorted.Add(name);
            }

            projected["required"] = sorted;
        }

        if (value["enum"] is JsonArray enumeration)
        {
            JsonArray sorted = [];

            foreach (string member in enumeration
                .Select(node => node?.ToJsonString() ?? "null")
                .Order(StringComparer.Ordinal))
            {
                sorted.Add(JsonNode.Parse(member));
            }

            projected["enum"] = sorted;
        }

        if (value["properties"] is JsonObject properties)
        {
            projected["properties"] = ProjectNamed(properties, ProjectSchema);
        }

        if (value["items"] is { } items)
        {
            projected["items"] = ProjectSchema(items);
        }

        foreach (string composite in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (value[composite] is not JsonArray branches)
            {
                continue;
            }

            JsonArray projectedBranches = [];

            foreach (JsonNode? branch in branches)
            {
                projectedBranches.Add(ProjectSchema(branch));
            }

            projected[composite] = projectedBranches;
        }

        return projected;
    }

    /// <summary>Normalizes <c>type</c> to a sorted array of type names.</summary>
    private static JsonArray NormalizeTypes(JsonNode type)
    {
        List<string> names = type switch
        {
            JsonArray array => [.. array.Select(node => node?.GetValue<string>() ?? "null")],
            _ => [type.GetValue<string>()],
        };

        JsonArray normalized = [];

        foreach (string name in names.Order(StringComparer.Ordinal))
        {
            normalized.Add(name);
        }

        return normalized;
    }
}
