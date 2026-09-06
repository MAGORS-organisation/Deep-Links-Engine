namespace Dle.ContractTests.OpenApi;

/// <summary>Whether a difference between two contract projections can break a caller.</summary>
public enum ContractChangeKind
{
    /// <summary>The change can only be observed by a caller that opted into it.</summary>
    Additive = 0,

    /// <summary>The change can break an existing integration and must fail the build.</summary>
    Breaking = 1,
}

/// <summary>One difference between the committed contract and the generated one.</summary>
/// <param name="Kind">Whether the change breaks a caller.</param>
/// <param name="Location">Where in the contract it is, in a form a person can search for.</param>
/// <param name="Message">What changed, phrased so that the fix is obvious.</param>
public sealed record ContractChange(ContractChangeKind Kind, string Location, string Message)
{
    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"[{(Kind == ContractChangeKind.Breaking ? "BREAKING" : "additive")}] {Location}: {Message}");
}

/// <summary>
/// Compares two contract projections and classifies every difference (§C.8 "main: contract tests").
/// </summary>
/// <remarks>
/// <para>
/// The rules are the ones an SDK author would write down. Something the caller relies on being there
/// disappearing is breaking: a route, an operation, a response status, a media type, a schema, a
/// property, a security scheme, an enumeration member, a value the type used to admit. Something the
/// caller must now supply that it did not supply before is breaking too: a newly required property, a
/// newly required parameter, a request body that was optional and is not any more.
/// </para>
/// <para>
/// Everything else is additive. A new route, a new optional property, a new response code, a widened
/// type, a new enumeration member on a request: none of those can stop yesterday's client working, so
/// none of them should stop today's build. A gate that fires on additive change is a gate that gets
/// regenerated unread, and then it guards nothing.
/// </para>
/// <para>
/// The asymmetry between adding an enumeration member to a request and to a response is real, and
/// this differ deliberately does not model it — the document does not say which schemas are only ever
/// read and which are only ever written, and guessing would produce false alarms on the shared ones.
/// Additions are reported as additive; removals, which break both directions, are reported as
/// breaking.
/// </para>
/// </remarks>
public static class OpenApiContractDiff
{
    /// <summary>Compares a committed baseline against a freshly generated projection.</summary>
    /// <param name="baseline">The committed contract.</param>
    /// <param name="current">The generated contract.</param>
    /// <returns>Every difference, breaking ones first, each with a readable location.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<ContractChange> Compare(JsonObject baseline, JsonObject current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        List<ContractChange> changes = [];

        CompareOperations(
            baseline["operations"] as JsonObject ?? [],
            current["operations"] as JsonObject ?? [],
            changes);

        CompareSchemas(
            baseline["schemas"] as JsonObject ?? [],
            current["schemas"] as JsonObject ?? [],
            changes);

        CompareSecuritySchemes(
            baseline["securitySchemes"] as JsonObject ?? [],
            current["securitySchemes"] as JsonObject ?? [],
            changes);

        return [.. changes
            .OrderByDescending(change => change.Kind)
            .ThenBy(change => change.Location, StringComparer.Ordinal)];
    }

    /// <summary>Renders a change list as the assertion message a reviewer reads.</summary>
    /// <param name="changes">The changes.</param>
    /// <returns>One line per change, breaking ones first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="changes"/> is <see langword="null"/>.</exception>
    public static string Describe(IReadOnlyList<ContractChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        return string.Join(Environment.NewLine, changes.Select(change => "  " + change));
    }

    private static void CompareOperations(JsonObject baseline, JsonObject current, List<ContractChange> changes)
    {
        foreach ((string key, JsonNode? node) in baseline)
        {
            if (current[key] is not JsonObject present)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Breaking,
                    key,
                    "the operation was removed; a caller bound to it now receives 404 or 405."));

                continue;
            }

            if (node is not JsonObject was)
            {
                continue;
            }

            CompareOperation(key, was, present, changes);
        }

        foreach (string key in current.Select(entry => entry.Key))
        {
            if (baseline[key] is null)
            {
                changes.Add(new ContractChange(ContractChangeKind.Additive, key, "a new operation was added."));
            }
        }
    }

    private static void CompareOperation(
        string operation,
        JsonObject baseline,
        JsonObject current,
        List<ContractChange> changes)
    {
        string wasId = baseline["operationId"]?.GetValue<string>() ?? string.Empty;
        string isId = current["operationId"]?.GetValue<string>() ?? string.Empty;

        if (!string.Equals(wasId, isId, StringComparison.Ordinal))
        {
            // The operation identifier is the method name in every generated client.
            changes.Add(new ContractChange(
                ContractChangeKind.Breaking,
                operation,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the operationId changed from '{wasId}' to '{isId}'; every generated client renames its method.")));
        }

        CompareParameters(operation, baseline["parameters"] as JsonObject ?? [], current["parameters"] as JsonObject ?? [], changes);
        CompareRequestBody(operation, baseline["requestBody"] as JsonObject, current["requestBody"] as JsonObject, changes);
        CompareResponses(operation, baseline["responses"] as JsonObject ?? [], current["responses"] as JsonObject ?? [], changes);
    }

    private static void CompareParameters(
        string operation,
        JsonObject baseline,
        JsonObject current,
        List<ContractChange> changes)
    {
        foreach ((string key, JsonNode? node) in baseline)
        {
            string location = string.Create(CultureInfo.InvariantCulture, $"{operation} parameter {key}");

            if (current[key] is not JsonObject present)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Breaking,
                    location,
                    "the parameter was removed; a caller that still sends it is now ignored or refused."));

                continue;
            }

            if (node is not JsonObject was)
            {
                continue;
            }

            bool wasRequired = was["required"]?.GetValue<bool>() ?? false;
            bool isRequired = present["required"]?.GetValue<bool>() ?? false;

            if (!wasRequired && isRequired)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Breaking,
                    location,
                    "the parameter became required; every existing caller that omits it now fails."));
            }

            CompareSchema(location, was["schema"], present["schema"], changes);
        }

        foreach ((string key, JsonNode? node) in current)
        {
            if (baseline[key] is not null)
            {
                continue;
            }

            bool required = (node as JsonObject)?["required"]?.GetValue<bool>() ?? false;
            string location = string.Create(CultureInfo.InvariantCulture, $"{operation} parameter {key}");

            changes.Add(required
                ? new ContractChange(
                    ContractChangeKind.Breaking,
                    location,
                    "a new required parameter was added; every existing caller omits it.")
                : new ContractChange(ContractChangeKind.Additive, location, "a new optional parameter was added."));
        }
    }

    private static void CompareRequestBody(
        string operation,
        JsonObject? baseline,
        JsonObject? current,
        List<ContractChange> changes)
    {
        string location = string.Create(CultureInfo.InvariantCulture, $"{operation} requestBody");

        if (baseline is null)
        {
            if (current is not null && (current["required"]?.GetValue<bool>() ?? false))
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Breaking,
                    location,
                    "the operation now requires a request body it previously took none of."));
            }

            return;
        }

        if (current is null)
        {
            changes.Add(new ContractChange(
                ContractChangeKind.Breaking,
                location,
                "the request body was removed; the payload a caller sends is no longer read."));

            return;
        }

        if (!(baseline["required"]?.GetValue<bool>() ?? false) && (current["required"]?.GetValue<bool>() ?? false))
        {
            changes.Add(new ContractChange(
                ContractChangeKind.Breaking,
                location,
                "the request body became required."));
        }

        CompareContent(location, baseline["content"] as JsonObject ?? [], current["content"] as JsonObject ?? [], changes);
    }

    private static void CompareResponses(
        string operation,
        JsonObject baseline,
        JsonObject current,
        List<ContractChange> changes)
    {
        foreach ((string status, JsonNode? node) in baseline)
        {
            string location = string.Create(CultureInfo.InvariantCulture, $"{operation} response {status}");

            if (current[status] is not JsonObject present)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Breaking,
                    location,
                    "the documented response was removed; a caller branching on this status has nothing to branch on."));

                continue;
            }

            CompareContent(
                location,
                (node as JsonObject)?["content"] as JsonObject ?? [],
                present["content"] as JsonObject ?? [],
                changes);
        }

        foreach (string status in current.Select(entry => entry.Key))
        {
            if (baseline[status] is null)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Additive,
                    string.Create(CultureInfo.InvariantCulture, $"{operation} response {status}"),
                    "a new response status was documented."));
            }
        }
    }

    private static void CompareContent(
        string location,
        JsonObject baseline,
        JsonObject current,
        List<ContractChange> changes)
    {
        foreach ((string mediaType, JsonNode? node) in baseline)
        {
            string where = string.Create(CultureInfo.InvariantCulture, $"{location} ({mediaType})");

            if (current[mediaType] is null)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Breaking,
                    where,
                    "the media type is no longer produced or accepted."));

                continue;
            }

            CompareSchema(where, node, current[mediaType], changes);
        }

        foreach (string mediaType in current.Select(entry => entry.Key))
        {
            if (baseline[mediaType] is null)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Additive,
                    string.Create(CultureInfo.InvariantCulture, $"{location} ({mediaType})"),
                    "a new media type was added."));
            }
        }
    }

    private static void CompareSchemas(JsonObject baseline, JsonObject current, List<ContractChange> changes)
    {
        foreach ((string name, JsonNode? node) in baseline)
        {
            string location = string.Create(CultureInfo.InvariantCulture, $"schema {name}");

            if (current[name] is null)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Breaking,
                    location,
                    "the schema was removed; every operation and generated model naming it is dangling."));

                continue;
            }

            CompareSchema(location, node, current[name], changes);
        }

        foreach (string name in current.Select(entry => entry.Key))
        {
            if (baseline[name] is null)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Additive,
                    string.Create(CultureInfo.InvariantCulture, $"schema {name}"),
                    "a new schema was added."));
            }
        }
    }

    private static void CompareSecuritySchemes(JsonObject baseline, JsonObject current, List<ContractChange> changes)
    {
        foreach ((string name, JsonNode? node) in baseline)
        {
            string location = string.Create(CultureInfo.InvariantCulture, $"securityScheme {name}");

            if (current[name] is not JsonObject present)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Breaking,
                    location,
                    "the credential scheme was removed; callers presenting it are no longer authenticated."));

                continue;
            }

            if (node is JsonObject was && !JsonNode.DeepEquals(was, present))
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Breaking,
                    location,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"the credential is presented differently: was {was.ToJsonString()}, is {present.ToJsonString()}.")));
            }
        }

        foreach (string name in current.Select(entry => entry.Key))
        {
            if (baseline[name] is null)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Additive,
                    string.Create(CultureInfo.InvariantCulture, $"securityScheme {name}"),
                    "a new credential scheme was added."));
            }
        }
    }

    /// <summary>
    /// Compares two schemas member by member.
    /// </summary>
    /// <remarks>
    /// The type comparison is a set comparison, which is what makes "narrowed type" detectable at all:
    /// <c>["null","string"]</c> becoming <c>["string"]</c> removes a value the caller was told it could
    /// receive, and a caller written against the old document may well be passing null.
    /// </remarks>
    private static void CompareSchema(string location, JsonNode? baseline, JsonNode? current, List<ContractChange> changes)
    {
        if (baseline is not JsonObject was)
        {
            return;
        }

        if (current is not JsonObject present)
        {
            changes.Add(new ContractChange(
                ContractChangeKind.Breaking,
                location,
                "the schema disappeared where one was published."));

            return;
        }

        CompareReference(location, was, present, changes);
        CompareTypes(location, was, present, changes);
        CompareEnumeration(location, was, present, changes);
        CompareRequired(location, was, present, changes);
        CompareProperties(location, was, present, changes);
        CompareFormat(location, was, present, changes);

        if (was["items"] is { } wasItems)
        {
            CompareSchema(location + " items", wasItems, present["items"], changes);
        }
    }

    private static void CompareReference(string location, JsonObject was, JsonObject present, List<ContractChange> changes)
    {
        string? wasRef = was["$ref"]?.GetValue<string>();
        string? isRef = present["$ref"]?.GetValue<string>();

        if (!string.Equals(wasRef, isRef, StringComparison.Ordinal))
        {
            changes.Add(new ContractChange(
                ContractChangeKind.Breaking,
                location,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the referenced schema changed from '{wasRef ?? "(inline)"}' to '{isRef ?? "(inline)"}'.")));
        }
    }

    private static void CompareTypes(string location, JsonObject was, JsonObject present, List<ContractChange> changes)
    {
        HashSet<string> wasTypes = TypeSet(was);
        HashSet<string> isTypes = TypeSet(present);

        if (wasTypes.Count == 0 && isTypes.Count == 0)
        {
            return;
        }

        List<string> removed = [.. wasTypes.Except(isTypes, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        List<string> added = [.. isTypes.Except(wasTypes, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        if (removed.Count > 0)
        {
            changes.Add(new ContractChange(
                ContractChangeKind.Breaking,
                location,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the type was narrowed: it no longer admits {string.Join(", ", removed)}.")));
        }

        if (added.Count > 0)
        {
            changes.Add(new ContractChange(
                ContractChangeKind.Additive,
                location,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the type was widened to also admit {string.Join(", ", added)}.")));
        }
    }

    private static void CompareFormat(string location, JsonObject was, JsonObject present, List<ContractChange> changes)
    {
        string? wasFormat = was["format"]?.GetValue<string>();
        string? isFormat = present["format"]?.GetValue<string>();

        if (string.Equals(wasFormat, isFormat, StringComparison.Ordinal))
        {
            return;
        }

        // A format is a promise about the lexical shape of a value. Changing or dropping it is a
        // narrowing for anything that parses the value, which is what an SDK does.
        changes.Add(new ContractChange(
            ContractChangeKind.Breaking,
            location,
            string.Create(
                CultureInfo.InvariantCulture,
                $"the format changed from '{wasFormat ?? "(none)"}' to '{isFormat ?? "(none)"}'.")));
    }

    private static void CompareEnumeration(string location, JsonObject was, JsonObject present, List<ContractChange> changes)
    {
        if (was["enum"] is not JsonArray wasValues)
        {
            if (present["enum"] is JsonArray)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Breaking,
                    location,
                    "the value became a closed enumeration; values that used to be accepted may now be refused."));
            }

            return;
        }

        HashSet<string> before = [.. wasValues.Select(node => node?.ToJsonString() ?? "null")];
        HashSet<string> after = present["enum"] is JsonArray isValues
            ? [.. isValues.Select(node => node?.ToJsonString() ?? "null")]
            : [];

        List<string> removed = [.. before.Except(after, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        List<string> added = [.. after.Except(before, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        if (removed.Count > 0)
        {
            changes.Add(new ContractChange(
                ContractChangeKind.Breaking,
                location,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the enumeration no longer contains {string.Join(", ", removed)}.")));
        }

        if (added.Count > 0)
        {
            changes.Add(new ContractChange(
                ContractChangeKind.Additive,
                location,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the enumeration gained {string.Join(", ", added)}.")));
        }
    }

    private static void CompareRequired(string location, JsonObject was, JsonObject present, List<ContractChange> changes)
    {
        HashSet<string> before = RequiredSet(was);
        HashSet<string> after = RequiredSet(present);

        foreach (string name in after.Except(before, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            changes.Add(new ContractChange(
                ContractChangeKind.Breaking,
                location,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{name}' became required; every payload written against the previous contract omits it.")));
        }
    }

    private static void CompareProperties(string location, JsonObject was, JsonObject present, List<ContractChange> changes)
    {
        if (was["properties"] is not JsonObject before)
        {
            return;
        }

        JsonObject after = present["properties"] as JsonObject ?? [];

        foreach ((string name, JsonNode? node) in before)
        {
            string where = string.Create(CultureInfo.InvariantCulture, $"{location}.{name}");

            if (after[name] is null)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Breaking,
                    where,
                    "the property was removed or renamed; every client that reads or writes it breaks silently."));

                continue;
            }

            CompareSchema(where, node, after[name], changes);
        }

        foreach (string name in after.Select(entry => entry.Key))
        {
            if (before[name] is null)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.Additive,
                    string.Create(CultureInfo.InvariantCulture, $"{location}.{name}"),
                    "a new property was added."));
            }
        }
    }

    private static HashSet<string> TypeSet(JsonObject schema) =>
        schema["type"] is JsonArray types
            ? [.. types.Select(node => node?.GetValue<string>() ?? "null")]
            : schema["type"] is { } single
                ? [single.GetValue<string>()]
                : [];

    private static HashSet<string> RequiredSet(JsonObject schema) =>
        schema["required"] is JsonArray required
            ? [.. required.Select(node => node?.GetValue<string>() ?? string.Empty)]
            : [];
}
