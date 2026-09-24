using System.Reflection;
using System.Text;

using Dle.Control.Features.Shared;
using Dle.Domain.Entities;
using Dle.Persistence.Repositories;

using Microsoft.AspNetCore.Http;

using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// <c>Idempotency-Key</c> has to make a retried write safe and an accidentally reused key loud: the
/// same key with the same body replays the stored response, the same key with a different body is a
/// conflict rather than a second write (§B.7.3).
/// </summary>
/// <remarks>
/// <para>
/// The decision is split across two pieces of production code — the filter derives the request hash
/// and the store compares it against what was recorded — and both are private static methods on
/// types whose public entry points need a database. The whole path through
/// <see cref="IdempotencyStore.BeginAsync"/> therefore belongs to the integration suite, which needs
/// a real Postgres for the unique index that makes the reservation atomic.
/// </para>
/// <para>
/// What can be decided without a database is the pair of functions that produce the answer, and they
/// are what is tested here: the hash the filter computes from a request, and the outcome the store
/// derives from a stored row and that hash. They are reached by reflection rather than being
/// re-implemented — a re-implementation would pass while the real code was wrong, which is the one
/// thing a test of this kind must not do.
/// </para>
/// </remarks>
public sealed class IdempotencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SameKeyAndSameBody_ReplaysTheStoredResponse()
    {
        byte[] hash = await RequestHashAsync("POST /api/v1/links", "/api/v1/links", body: """{"slug":"summer"}""");

        IdempotencyRecord stored = Stored(hash, status: 201, body: """{"id":"77"}""");

        IdempotencyLookup lookup = Evaluate(stored, hash, Now);

        Assert.Equal(IdempotencyOutcome.Replay, lookup.Outcome);
        Assert.Equal(201, lookup.ResponseStatus);
        Assert.Equal("""{"id":"77"}""", lookup.ResponseBody);
    }

    [Fact]
    public async Task SameKeyAndDifferentBody_IsAConflictRatherThanASecondWrite()
    {
        byte[] first = await RequestHashAsync("POST /api/v1/links", "/api/v1/links", body: """{"slug":"summer"}""");
        byte[] second = await RequestHashAsync("POST /api/v1/links", "/api/v1/links", body: """{"slug":"winter"}""");

        Assert.NotEqual(first, second);

        IdempotencyRecord stored = Stored(first, status: 201, body: """{"id":"77"}""");

        // Reusing one key for two requests is a client mistake, and replaying the first response
        // would hide it. The stored response is deliberately not returned.
        Assert.Equal(IdempotencyOutcome.Conflict, Evaluate(stored, second, Now).Outcome);
    }

    [Fact]
    public async Task SameKeyOnADifferentRoute_HashesDifferently()
    {
        byte[] links = await RequestHashAsync("POST /api/v1/links", "/api/v1/links", body: """{"slug":"summer"}""");
        byte[] bulk = await RequestHashAsync("POST /api/v1/links/bulk", "/api/v1/links/bulk", body: """{"slug":"summer"}""");

        Assert.NotEqual(links, bulk);
    }

    [Fact]
    public async Task SameKeyAndBodyWithADifferentQueryString_HashesDifferently()
    {
        byte[] plain = await RequestHashAsync("POST /api/v1/links", "/api/v1/links", body: """{"slug":"s"}""");

        byte[] withQuery = await RequestHashAsync(
            "POST /api/v1/links",
            "/api/v1/links",
            body: """{"slug":"s"}""",
            queryString: "?dry_run=true");

        Assert.NotEqual(plain, withQuery);
    }

    [Fact]
    public async Task TheSameRequestTwice_HashesIdentically()
    {
        byte[] first = await RequestHashAsync("POST /api/v1/links", "/api/v1/links", body: """{"slug":"summer"}""");
        byte[] second = await RequestHashAsync("POST /api/v1/links", "/api/v1/links", body: """{"slug":"summer"}""");

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ReadingTheBodyForTheHash_LeavesItReadableForTheHandler()
    {
        var context = Request("/api/v1/links", """{"slug":"summer"}""", queryString: null);

        _ = await ComputeHashAsync(context, "POST /api/v1/links");

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        string body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        // The filter rewinds the buffered body; without that the endpoint would receive nothing.
        Assert.Equal("""{"slug":"summer"}""", body);
    }

    [Fact]
    public async Task AFirstAttemptStillRunning_IsRefusedRatherThanRunTwice()
    {
        byte[] hash = await RequestHashAsync("POST /api/v1/links", "/api/v1/links", body: """{"slug":"summer"}""");

        // Status zero is the reservation the filter writes before the handler runs.
        IdempotencyRecord reserved = Stored(hash, status: 0, body: null);

        Assert.Equal(IdempotencyOutcome.InProgress, Evaluate(reserved, hash, Now).Outcome);
    }

    [Fact]
    public async Task AnExpiredRecord_LetsTheRequestProceedAfresh()
    {
        byte[] hash = await RequestHashAsync("POST /api/v1/links", "/api/v1/links", body: """{"slug":"summer"}""");

        IdempotencyRecord stored = Stored(hash, status: 201, body: """{"id":"77"}""");
        stored.ExpiresAt = Now.AddSeconds(-1);

        // Past its retention the key is simply unknown again — including for a different body, which
        // is why the expiry test comes before the hash comparison.
        Assert.Equal(IdempotencyOutcome.Proceed, Evaluate(stored, hash, Now).Outcome);
    }

    [Fact]
    public void TheHashComparison_IsFixedTime()
    {
        MethodInfo evaluate = EvaluateMethod;

        // The stored hash is derived from a request body an attacker may control; comparing it with
        // an early exit would leak it byte by byte.
        Assert.True(
            Dle.UnitTests.Crypto.ConstantTimeAssertion.CallsFixedTimeEquals(typeof(IdempotencyStore), evaluate.Name),
            "IdempotencyStore.Evaluate must compare request hashes with CryptographicOperations.FixedTimeEquals.");
    }

    [Fact]
    public void HeaderName_IsTheDocumentedOne() =>
        Assert.Equal("Idempotency-Key", IdempotencyFilter.HeaderName);

    private static IdempotencyRecord Stored(byte[] requestHash, int status, string? body) => new()
    {
        Key = "key-1",
        Endpoint = "POST /api/v1/links",
        RequestHash = requestHash,
        ResponseStatus = status,
        ResponseBody = body,
        CreatedAt = Now.AddMinutes(-1),
        ExpiresAt = Now.AddHours(24),
    };

    private static DefaultHttpContext Request(string path, string body, string? queryString)
    {
        var context = new DefaultHttpContext();

        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.ContentType = "application/json";

        if (queryString is not null)
        {
            context.Request.QueryString = new QueryString(queryString);
        }

        byte[] bytes = Encoding.UTF8.GetBytes(body);

        context.Request.Body = new MemoryStream(bytes, writable: false);
        context.Request.ContentLength = bytes.Length;

        return context;
    }

    private static async Task<byte[]> RequestHashAsync(
        string endpoint,
        string path,
        string body,
        string? queryString = null) =>
        await ComputeHashAsync(Request(path, body, queryString), endpoint);

    private static readonly MethodInfo ComputeRequestHashMethod =
        typeof(IdempotencyFilter).GetMethod(
            "ComputeRequestHashAsync",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("IdempotencyFilter.ComputeRequestHashAsync no longer exists.");

    private static readonly MethodInfo EvaluateMethod =
        typeof(IdempotencyStore).GetMethod("Evaluate", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("IdempotencyStore.Evaluate no longer exists.");

    private static async Task<byte[]> ComputeHashAsync(HttpContext context, string endpoint) =>
        await (Task<byte[]>)ComputeRequestHashMethod.Invoke(null, [context, endpoint])!;

    private static IdempotencyLookup Evaluate(IdempotencyRecord record, byte[] requestHash, DateTimeOffset now) =>
        (IdempotencyLookup)EvaluateMethod.Invoke(null, [record, requestHash, now])!;
}
