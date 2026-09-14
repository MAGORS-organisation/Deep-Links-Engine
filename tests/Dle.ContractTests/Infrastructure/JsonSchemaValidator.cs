using System.Text.RegularExpressions;

namespace Dle.ContractTests.Infrastructure;

/// <summary>One reason an instance did not satisfy a schema.</summary>
/// <param name="InstancePath">JSON Pointer to the offending value.</param>
/// <param name="Keyword">The schema keyword that rejected it.</param>
/// <param name="Message">What is wrong, in a sentence.</param>
public sealed record SchemaViolation(string InstancePath, string Keyword, string Message)
{
    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{(InstancePath.Length == 0 ? "(root)" : InstancePath)} [{Keyword}] {Message}");
}

/// <summary>
/// A small JSON Schema 2020-12 validator, covering the keywords the committed schemas use.
/// </summary>
/// <remarks>
/// <para>
/// Written rather than taken off the shelf for one reason: NFR-18 makes licence purity a build gate,
/// and the dependency set of this repository is centrally managed and deliberately short. A contract
/// test suite that needs a new third-party package in order to check a hundred lines of schema is a
/// bad trade — particularly when the alternative is three hundred lines that do exactly what these
/// schemas need and nothing else.
/// </para>
/// <para>
/// It is deliberately a subset: <c>$ref</c> into the same document, <c>type</c>, <c>enum</c>,
/// <c>const</c>, the object keywords, the array keywords, the string and number bounds, the boolean
/// combinators and the two formats that matter here. It is strict about what it does not implement —
/// an unknown keyword that could restrict an instance raises a violation rather than being ignored,
/// so a schema cannot quietly stop testing anything.
/// </para>
/// </remarks>
public sealed class JsonSchemaValidator
{
    /// <summary>Keywords this validator implements, plus the annotations it may safely ignore.</summary>
    private static readonly HashSet<string> KnownKeywords =
    [
        "$schema", "$id", "$defs", "$ref", "$comment",
        "title", "description", "examples", "default", "deprecated", "readOnly", "writeOnly",
        "type", "enum", "const", "format",
        "properties", "required", "additionalProperties", "patternProperties", "propertyNames",
        "minProperties", "maxProperties",
        "items", "prefixItems", "minItems", "maxItems", "uniqueItems", "contains", "minContains", "maxContains",
        "minLength", "maxLength", "pattern",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "allOf", "anyOf", "oneOf", "not",
    ];

    private readonly JsonNode _root;

    /// <summary>Creates a validator over one schema document.</summary>
    /// <param name="schema">The schema, parsed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is <see langword="null"/>.</exception>
    public JsonSchemaValidator(JsonNode schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        _root = schema;
    }

    /// <summary>Creates a validator from schema text.</summary>
    /// <param name="json">The schema document.</param>
    /// <returns>The validator.</returns>
    /// <exception cref="InvalidOperationException">The text is not valid JSON.</exception>
    public static JsonSchemaValidator Parse(string json) =>
        new(JsonNode.Parse(json) ?? throw new InvalidOperationException("The schema is not valid JSON."));

    /// <summary>Validates an instance.</summary>
    /// <param name="instance">The document to check.</param>
    /// <returns>Every violation found; empty when the instance is valid.</returns>
    public IReadOnlyList<SchemaViolation> Validate(JsonNode? instance)
    {
        List<SchemaViolation> violations = [];

        Validate(instance, _root, string.Empty, violations);

        return violations;
    }

    /// <summary>Validates an instance given as text.</summary>
    /// <param name="json">The document to check.</param>
    /// <returns>Every violation found; empty when the instance is valid.</returns>
    public IReadOnlyList<SchemaViolation> ValidateJson(string json) => Validate(JsonNode.Parse(json));

    /// <summary>Renders violations as an assertion message.</summary>
    /// <param name="violations">The violations.</param>
    /// <returns>One line per violation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="violations"/> is <see langword="null"/>.</exception>
    public static string Describe(IReadOnlyList<SchemaViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);

        return string.Join(Environment.NewLine, violations.Select(violation => "  " + violation));
    }

    private void Validate(JsonNode? instance, JsonNode schema, string path, List<SchemaViolation> violations)
    {
        if (schema is JsonValue literal && literal.TryGetValue(out bool allowAll))
        {
            if (!allowAll)
            {
                violations.Add(new SchemaViolation(path, "false", "the schema accepts nothing here."));
            }

            return;
        }

        if (schema is not JsonObject rules)
        {
            violations.Add(new SchemaViolation(path, "$schema", "the schema is neither an object nor a boolean."));
            return;
        }

        RejectUnknownKeywords(rules, path, violations);

        if (rules["$ref"]?.GetValue<string>() is { } reference)
        {
            Validate(instance, Resolve(reference), path, violations);
            return;
        }

        ValidateType(instance, rules, path, violations);
        ValidateEnumeration(instance, rules, path, violations);
        ValidateObject(instance as JsonObject, rules, path, violations);
        ValidateArray(instance as JsonArray, rules, path, violations);
        ValidateString(instance, rules, path, violations);
        ValidateNumber(instance, rules, path, violations);
        ValidateCombinators(instance, rules, path, violations);
    }

    private static void RejectUnknownKeywords(JsonObject rules, string path, List<SchemaViolation> violations)
    {
        foreach (string keyword in rules.Select(entry => entry.Key))
        {
            if (!KnownKeywords.Contains(keyword))
            {
                violations.Add(new SchemaViolation(
                    path,
                    keyword,
                    "the committed schema uses a keyword this validator does not implement, so the "
                    + "constraint would silently not be checked."));
            }
        }
    }

    private JsonNode Resolve(string reference)
    {
        if (!reference.StartsWith('#'))
        {
            throw new InvalidOperationException("Only same-document references are supported: " + reference);
        }

        JsonNode current = _root;

        foreach (string segment in reference.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string decoded = segment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);

            current = current[decoded]
                ?? throw new InvalidOperationException("The reference " + reference + " does not resolve.");
        }

        return current;
    }

    private static void ValidateType(JsonNode? instance, JsonObject rules, string path, List<SchemaViolation> violations)
    {
        if (rules["type"] is not { } type)
        {
            return;
        }

        List<string> allowed = type is JsonArray array
            ? [.. array.Select(node => node?.GetValue<string>() ?? "null")]
            : [type.GetValue<string>()];

        string actual = KindOf(instance);

        bool ok = allowed.Any(name => name switch
        {
            "integer" => string.Equals(actual, "integer", StringComparison.Ordinal),
            "number" => actual is "integer" or "number",
            _ => string.Equals(actual, name, StringComparison.Ordinal),
        });

        if (!ok)
        {
            violations.Add(new SchemaViolation(
                path,
                "type",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"expected {string.Join(" or ", allowed)}, found {actual}.")));
        }
    }

    private static void ValidateEnumeration(
        JsonNode? instance,
        JsonObject rules,
        string path,
        List<SchemaViolation> violations)
    {
        if (rules["enum"] is JsonArray allowed
            && !allowed.Any(candidate => JsonNode.DeepEquals(candidate, instance)))
        {
            violations.Add(new SchemaViolation(
                path,
                "enum",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{instance?.ToJsonString() ?? "null"} is not one of {allowed.ToJsonString()}.")));
        }

        if (rules["const"] is { } constant && !JsonNode.DeepEquals(constant, instance))
        {
            violations.Add(new SchemaViolation(
                path,
                "const",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"expected {constant.ToJsonString()}, found {instance?.ToJsonString() ?? "null"}.")));
        }
    }

    private void ValidateObject(JsonObject? instance, JsonObject rules, string path, List<SchemaViolation> violations)
    {
        if (instance is null)
        {
            return;
        }

        if (rules["required"] is JsonArray required)
        {
            foreach (JsonNode? node in required)
            {
                string name = node?.GetValue<string>() ?? string.Empty;

                if (instance[name] is null && !instance.ContainsKey(name))
                {
                    violations.Add(new SchemaViolation(path, "required", "'" + name + "' is missing."));
                }
            }
        }

        JsonObject? properties = rules["properties"] as JsonObject;
        JsonObject? patternProperties = rules["patternProperties"] as JsonObject;

        foreach ((string name, JsonNode? value) in instance)
        {
            bool matched = false;

            if (properties?[name] is { } propertySchema)
            {
                matched = true;
                Validate(value, propertySchema, path + "/" + name, violations);
            }

            if (patternProperties is not null)
            {
                foreach ((string pattern, JsonNode? patternSchema) in patternProperties)
                {
                    if (!IsMatch(pattern, name) || patternSchema is null)
                    {
                        continue;
                    }

                    matched = true;
                    Validate(value, patternSchema, path + "/" + name, violations);
                }
            }

            if (rules["propertyNames"] is { } nameSchema)
            {
                Validate(JsonValue.Create(name), nameSchema, path + "/" + name, violations);
            }

            if (matched || rules["additionalProperties"] is not { } additional)
            {
                continue;
            }

            if (additional is JsonValue flag && flag.TryGetValue(out bool allowed) && !allowed)
            {
                violations.Add(new SchemaViolation(
                    path,
                    "additionalProperties",
                    "'" + name + "' is not declared and additional properties are not allowed."));
            }
            else if (additional is JsonObject)
            {
                Validate(value, additional, path + "/" + name, violations);
            }
        }

        if (rules["minProperties"]?.GetValue<int>() is { } minimum && instance.Count < minimum)
        {
            violations.Add(new SchemaViolation(
                path,
                "minProperties",
                string.Create(CultureInfo.InvariantCulture, $"expected at least {minimum} members, found {instance.Count}.")));
        }

        if (rules["maxProperties"]?.GetValue<int>() is { } maximum && instance.Count > maximum)
        {
            violations.Add(new SchemaViolation(
                path,
                "maxProperties",
                string.Create(CultureInfo.InvariantCulture, $"expected at most {maximum} members, found {instance.Count}.")));
        }
    }

    private void ValidateArray(JsonArray? instance, JsonObject rules, string path, List<SchemaViolation> violations)
    {
        if (instance is null)
        {
            return;
        }

        if (rules["prefixItems"] is JsonArray prefix)
        {
            for (int i = 0; i < prefix.Count && i < instance.Count; i++)
            {
                Validate(instance[i], prefix[i]!, Index(path, i), violations);
            }
        }

        if (rules["items"] is { } items)
        {
            int start = rules["prefixItems"] is JsonArray declared ? declared.Count : 0;

            for (int i = start; i < instance.Count; i++)
            {
                Validate(instance[i], items, Index(path, i), violations);
            }
        }

        if (rules["minItems"]?.GetValue<int>() is { } minimum && instance.Count < minimum)
        {
            violations.Add(new SchemaViolation(
                path,
                "minItems",
                string.Create(CultureInfo.InvariantCulture, $"expected at least {minimum} items, found {instance.Count}.")));
        }

        if (rules["maxItems"]?.GetValue<int>() is { } maximum && instance.Count > maximum)
        {
            violations.Add(new SchemaViolation(
                path,
                "maxItems",
                string.Create(CultureInfo.InvariantCulture, $"expected at most {maximum} items, found {instance.Count}.")));
        }

        if (rules["uniqueItems"]?.GetValue<bool>() == true)
        {
            List<string> rendered = [.. instance.Select(node => node?.ToJsonString() ?? "null")];

            if (rendered.Distinct(StringComparer.Ordinal).Count() != rendered.Count)
            {
                violations.Add(new SchemaViolation(path, "uniqueItems", "the array contains a duplicate."));
            }
        }

        if (rules["contains"] is not { } contains)
        {
            return;
        }

        int matches = instance.Count(item => Validate(item, contains).Count == 0);
        int atLeast = rules["minContains"]?.GetValue<int>() ?? 1;

        if (matches < atLeast)
        {
            violations.Add(new SchemaViolation(
                path,
                "contains",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"expected at least {atLeast} item(s) matching the 'contains' schema, found {matches}.")));
        }

        if (rules["maxContains"]?.GetValue<int>() is { } atMost && matches > atMost)
        {
            violations.Add(new SchemaViolation(
                path,
                "maxContains",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"expected at most {atMost} item(s) matching the 'contains' schema, found {matches}.")));
        }
    }

    private static void ValidateString(
        JsonNode? instance,
        JsonObject rules,
        string path,
        List<SchemaViolation> violations)
    {
        if (instance is not JsonValue value || !value.TryGetValue(out string? text) || text is null)
        {
            return;
        }

        if (rules["minLength"]?.GetValue<int>() is { } minimum && text.Length < minimum)
        {
            violations.Add(new SchemaViolation(
                path,
                "minLength",
                string.Create(CultureInfo.InvariantCulture, $"expected at least {minimum} characters, found {text.Length}.")));
        }

        if (rules["maxLength"]?.GetValue<int>() is { } maximum && text.Length > maximum)
        {
            violations.Add(new SchemaViolation(
                path,
                "maxLength",
                string.Create(CultureInfo.InvariantCulture, $"expected at most {maximum} characters, found {text.Length}.")));
        }

        if (rules["pattern"]?.GetValue<string>() is { } pattern && !IsMatch(pattern, text))
        {
            violations.Add(new SchemaViolation(path, "pattern", "'" + text + "' does not match /" + pattern + "/."));
        }

        if (rules["format"]?.GetValue<string>() is { } format && !MatchesFormat(format, text))
        {
            violations.Add(new SchemaViolation(path, "format", "'" + text + "' is not a valid " + format + "."));
        }
    }

    private static void ValidateNumber(
        JsonNode? instance,
        JsonObject rules,
        string path,
        List<SchemaViolation> violations)
    {
        if (instance is not JsonValue value || !TryGetNumber(value, out decimal number))
        {
            return;
        }

        if (rules["minimum"]?.GetValue<decimal>() is { } minimum && number < minimum)
        {
            violations.Add(new SchemaViolation(
                path,
                "minimum",
                string.Create(CultureInfo.InvariantCulture, $"{number} is below the minimum of {minimum}.")));
        }

        if (rules["maximum"]?.GetValue<decimal>() is { } maximum && number > maximum)
        {
            violations.Add(new SchemaViolation(
                path,
                "maximum",
                string.Create(CultureInfo.InvariantCulture, $"{number} is above the maximum of {maximum}.")));
        }

        if (rules["exclusiveMinimum"]?.GetValue<decimal>() is { } exclusiveMinimum && number <= exclusiveMinimum)
        {
            violations.Add(new SchemaViolation(
                path,
                "exclusiveMinimum",
                string.Create(CultureInfo.InvariantCulture, $"{number} is not above {exclusiveMinimum}.")));
        }

        if (rules["exclusiveMaximum"]?.GetValue<decimal>() is { } exclusiveMaximum && number >= exclusiveMaximum)
        {
            violations.Add(new SchemaViolation(
                path,
                "exclusiveMaximum",
                string.Create(CultureInfo.InvariantCulture, $"{number} is not below {exclusiveMaximum}.")));
        }

        if (rules["multipleOf"]?.GetValue<decimal>() is { } divisor && divisor != 0 && number % divisor != 0)
        {
            violations.Add(new SchemaViolation(
                path,
                "multipleOf",
                string.Create(CultureInfo.InvariantCulture, $"{number} is not a multiple of {divisor}.")));
        }
    }

    private void ValidateCombinators(
        JsonNode? instance,
        JsonObject rules,
        string path,
        List<SchemaViolation> violations)
    {
        if (rules["allOf"] is JsonArray all)
        {
            foreach (JsonNode? branch in all)
            {
                Validate(instance, branch!, path, violations);
            }
        }

        if (rules["anyOf"] is JsonArray any
            && !any.Any(branch => Validate(instance, branch!).Count == 0))
        {
            violations.Add(new SchemaViolation(path, "anyOf", "the value matches none of the alternatives."));
        }

        if (rules["oneOf"] is JsonArray one)
        {
            int matches = one.Count(branch => Validate(instance, branch!).Count == 0);

            if (matches != 1)
            {
                violations.Add(new SchemaViolation(
                    path,
                    "oneOf",
                    string.Create(CultureInfo.InvariantCulture, $"expected exactly one alternative to match, {matches} did.")));
            }
        }

        if (rules["not"] is { } negated && Validate(instance, negated).Count == 0)
        {
            violations.Add(new SchemaViolation(path, "not", "the value matches a schema it must not match."));
        }
    }

    private List<SchemaViolation> Validate(JsonNode? instance, JsonNode schema)
    {
        List<SchemaViolation> violations = [];

        Validate(instance, schema, string.Empty, violations);

        return violations;
    }

    private static string Index(string path, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{path}/{index}");

    private static bool IsMatch(string pattern, string value) =>
        Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool MatchesFormat(string format, string value) => format switch
    {
        "date-time" => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out _),
        "uuid" => Guid.TryParseExact(value, "D", out _),
        "uri" => Uri.TryCreate(value, UriKind.Absolute, out _),

        // An unimplemented format is an annotation in JSON Schema 2020-12 and is not asserted on.
        _ => true,
    };

    private static bool TryGetNumber(JsonValue value, out decimal number)
    {
        if (value.TryGetValue(out decimal asDecimal))
        {
            number = asDecimal;
            return true;
        }

        number = 0;
        return false;
    }

    private static string KindOf(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue value when value.TryGetValue(out bool _) => "boolean",
        JsonValue value when value.TryGetValue(out string? _) => "string",
        JsonValue value when value.TryGetValue(out long _) => "integer",
        JsonValue value when value.TryGetValue(out decimal _) => "number",
        _ => "unknown",
    };
}
