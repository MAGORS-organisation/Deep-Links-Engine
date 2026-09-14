using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// The wire format the public API speaks: snake_case, string enums, nulls omitted
/// (SHARED-KERNEL §14).
/// </summary>
/// <remarks>
/// Reflection based rather than source generated, and only ever used by the tests. The product's own
/// serializers are source generated because SHARED-KERNEL §17.3 forbids reflection on the hot path;
/// a test that reads one response is not on it, and mirroring the source generated contexts here
/// would mean maintaining a second copy of every contract.
/// </remarks>
public static class WireJson
{
    /// <summary>Options matching the public wire format.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Reads a response body as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The shape to read.</typeparam>
    /// <param name="response">The response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deserialized body.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    public static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        return JsonSerializer.Deserialize<T>(body, Options);
    }

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));

        return options;
    }
}
