using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Dle.Control.Configuration;
using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Control.Infrastructure;

using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Links;

/// <summary>
/// <c>POST /api/v1/links/bulk</c> — a streamed batch of at least ten thousand links (FR-103).
/// </summary>
/// <remarks>
/// <para>
/// The request is newline delimited JSON and so is the response, which is the whole design. A ten
/// thousand row batch as a single JSON array would have to be parsed whole before the first row
/// could be created, and buffered whole before the first result could be returned; the client would
/// wait minutes with no idea whether row 4 000 succeeded. Line by line, the client sees the outcome
/// of row 4 000 the moment it happens, and neither side ever holds more than one row.
/// </para>
/// <para>
/// One bad row does not fail the batch. Each line is answered on its own with either the created
/// link or the problem type that stopped it, using exactly the same validation the single-link
/// endpoint runs — a rule that only the single-link path enforced would be a rule this endpoint
/// could be used to bypass, and a bulk import is precisely how somebody would try (§E.3).
/// </para>
/// <para>
/// Concurrency is bounded to two batches per tenant by the <c>dle-bulk</c> limiter and the row count
/// by <c>Dle:Control:BulkMaxRows</c>, which are the two §E.9 numbers for this endpoint.
/// </para>
/// </remarks>
public static class BulkCreateLinks
{
    /// <summary>Media type of a newline delimited JSON stream.</summary>
    public const string ContentType = "application/x-ndjson";

    /// <summary>How many result lines are written before the response is flushed.</summary>
    private const int FlushEvery = 50;

    /// <summary>Longest single line accepted, so one malformed row cannot exhaust memory.</summary>
    private const int MaxLineLength = 256 * 1024;

    /// <summary>Streams a batch of link creations.</summary>
    /// <param name="http">The request, which is read and written as it is processed.</param>
    /// <param name="writer">Validation, slug allocation and the write itself.</param>
    /// <param name="presenter">Builds the short URL of each created link.</param>
    /// <param name="audit">The administrative audit trail (FR-246).</param>
    /// <param name="cache">Drops the edge's negative cache entries for the hosts touched.</param>
    /// <param name="idempotency">Replay store for the <c>Idempotency-Key</c> header (§B.7.3).</param>
    /// <param name="jsonOptions">
    /// The HTTP serializer options, so a row in a batch is read exactly as the same body would be on
    /// the single-link route.
    /// </param>
    /// <param name="controlOptions">Control-plane options, for the row cap.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A streamed result; the response has already been written when it completes.</returns>
    public static async Task<IResult> HandleAsync(
        HttpContext http,
        LinkWriteService writer,
        LinkPresentation presenter,
        AuditLogWriter audit,
        ILinkCacheInvalidator cache,
        IdempotencyStore idempotency,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        IOptions<DleControlOptions> controlOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(idempotency);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentNullException.ThrowIfNull(controlOptions);

        DleCaller caller = http.RequireDleCaller();
        JsonSerializerOptions json = jsonOptions.Value.SerializerOptions;
        int maxRows = controlOptions.Value.BulkMaxRows;

        BulkReservation reservation =
            await ReserveAsync(http, idempotency, cancellationToken);

        if (reservation.Replay is not null)
        {
            return reservation.Replay;
        }

        http.Response.ContentType = ContentType;
        http.Response.StatusCode = StatusCodes.Status200OK;

        int created = 0;
        int failed = 0;
        int line = 0;
        HashSet<string> hosts = new(StringComparer.Ordinal);

        using StreamReader reader = new(
            http.Request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 8192,
            leaveOpen: true);

        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is string raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                line++;

                if (line > maxRows)
                {
                    await WriteLineAsync(
                        http,
                        json,
                        new BulkLinkResult
                        {
                            Ok = false,
                            Error = ProblemCodes.RateLimited,
                            Detail = string.Create(
                                CultureInfo.InvariantCulture,
                                $"The batch exceeds the maximum of {maxRows} rows (§E.9). "
                                + $"Everything up to that row was created; split the remainder into "
                                + $"another batch."),
                        },
                        cancellationToken);

                    failed++;
                    break;
                }

                if (raw.Length > MaxLineLength)
                {
                    await WriteLineAsync(
                        http,
                        json,
                        new BulkLinkResult
                        {
                            Ok = false,
                            Error = ProblemCodes.ValidationFailed,
                            Detail = "The row is too large to be a link definition.",
                        },
                        cancellationToken);

                    failed++;
                    continue;
                }

                BulkLinkItem? item;

                try
                {
                    item = JsonSerializer.Deserialize<BulkLinkItem>(raw, json);
                }
                catch (JsonException exception)
                {
                    await WriteLineAsync(
                        http,
                        json,
                        new BulkLinkResult
                        {
                            Ok = false,
                            Error = ProblemCodes.ValidationFailed,
                            Detail = exception.Message,
                        },
                        cancellationToken);

                    failed++;
                    continue;
                }

                if (item?.Link is null)
                {
                    await WriteLineAsync(
                        http,
                        json,
                        new BulkLinkResult
                        {
                            Ok = false,
                            Error = ProblemCodes.ValidationFailed,
                            Detail = "The row carries no link definition.",
                        },
                        cancellationToken);

                    failed++;
                    continue;
                }

                LinkWriteOutcome outcome =
                    await writer.CreateAsync(item.Link, caller, cancellationToken);

                if (outcome.Succeeded)
                {
                    Link createdLink = outcome.Link!;
                    string host = outcome.Host!;
                    hosts.Add(host);
                    created++;

                    await WriteLineAsync(
                        http,
                        json,
                        new BulkLinkResult
                        {
                            Ref = item.Ref,
                            Ok = true,
                            Id = createdLink.Id.ToString(CultureInfo.InvariantCulture),
                            ShortUrl = presenter.ShortUrl(host, createdLink.Slug),
                        },
                        cancellationToken);
                }
                else
                {
                    failed++;

                    await WriteLineAsync(
                        http,
                        json,
                        LinkWriteProblem.ToBulkLine(item.Ref, outcome),
                        cancellationToken);
                }

                if ((created + failed) % FlushEvery == 0)
                {
                    await http.Response.Body.FlushAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The client hung up mid-batch. Everything already written was created and the client
            // saw it; the reservation is released so the same key can carry the retry.
            await reservation.AbandonAsync(idempotency, CancellationToken.None);
            throw;
        }

        // One invalidation per host rather than one per row. A batch of ten thousand links on one
        // domain would otherwise be ten thousand cache round trips, and the only thing a create has
        // to drop is the negative entry the edge may hold for a slug nobody had asked for yet.
        foreach (string host in hosts)
        {
            await cache.InvalidateHostAsync(host, cancellationToken);
        }

        await AdministrativeAudit.RecordAsync(
            audit,
            caller,
            AuditActions.LinkBulkImported,
            AuditActions.LinkSubject,
            "batch",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["created"] = created.ToString(CultureInfo.InvariantCulture),
                ["failed"] = failed.ToString(CultureInfo.InvariantCulture),
                ["hosts"] = hosts.Count.ToString(CultureInfo.InvariantCulture),
            },
            cancellationToken);

        await reservation.CompleteAsync(idempotency, created, failed, cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);

        return Results.Empty;
    }

    /// <summary>
    /// Reserves the <c>Idempotency-Key</c> of a streamed batch, or replays its summary (§B.7.3).
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="idempotency">The replay store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reservation, carrying a replay result when the key has been seen before.</returns>
    /// <remarks>
    /// <para>
    /// This is the one write in the control plane whose idempotency does not hash the request body.
    /// Hashing it would mean buffering ten thousand rows before the first one could be created, which
    /// is exactly what the streaming shape exists to avoid, and storing the response for replay would
    /// mean keeping ten thousand result lines in a column.
    /// </para>
    /// <para>
    /// What is stored instead is the summary — how many rows were created and how many failed — and a
    /// repeat of the same key is answered with it rather than importing the batch a second time. The
    /// guarantee is therefore weaker and precisely stated: a retry never duplicates the batch, and a
    /// caller that reuses one key for two <em>different</em> batches gets the first batch's summary
    /// rather than a conflict, because the engine has not read enough to tell them apart.
    /// </para>
    /// </remarks>
    private static async Task<BulkReservation> ReserveAsync(
        HttpContext http,
        IdempotencyStore idempotency,
        CancellationToken cancellationToken)
    {
        if (!http.Request.Headers.TryGetValue(
                IdempotencyFilter.HeaderName,
                out Microsoft.Extensions.Primitives.StringValues values))
        {
            return BulkReservation.None;
        }

        string key = values.ToString().Trim();

        if (key.Length == 0)
        {
            return BulkReservation.None;
        }

        const string endpoint = "POST /api/v1/links/bulk";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(endpoint));

        IdempotencyLookup lookup =
            await idempotency.BeginAsync(key, endpoint, hash, cancellationToken);

        return lookup.Outcome switch
        {
            IdempotencyOutcome.Replay => new BulkReservation(
                key,
                endpoint,
                Results.Content(
                    lookup.ResponseBody ?? "{}",
                    "application/json",
                    Encoding.UTF8,
                    lookup.ResponseStatus)),

            IdempotencyOutcome.InProgress => new BulkReservation(
                key,
                endpoint,
                DleProblem.Conflict(
                    ProblemCodes.IdempotencyConflict,
                    "The first batch with this idempotency key has not finished.",
                    "An earlier batch carrying this Idempotency-Key is still being imported.")),

            IdempotencyOutcome.Conflict => new BulkReservation(
                key,
                endpoint,
                DleProblem.Conflict(
                    ProblemCodes.IdempotencyConflict,
                    "The idempotency key has already been used.",
                    "This Idempotency-Key was used for a different request. Use a fresh key.")),

            IdempotencyOutcome.Proceed or _ => new BulkReservation(key, endpoint, null),
        };
    }

    /// <summary>Writes one result line and its newline.</summary>
    /// <param name="http">The request being answered.</param>
    /// <param name="json">Serializer options shared with the rest of the API.</param>
    /// <param name="result">The line.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the line has been written.</returns>
    private static async Task WriteLineAsync(
        HttpContext http,
        JsonSerializerOptions json,
        BulkLinkResult result,
        CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(http.Response.Body, result, json, cancellationToken);
        await http.Response.Body.WriteAsync(NewLine, cancellationToken);
    }

    /// <summary>The single byte that separates two NDJSON records.</summary>
    private static ReadOnlyMemory<byte> NewLine { get; } = new([(byte)'\n']);

    /// <summary>The idempotency reservation held by a running batch.</summary>
    /// <param name="Key">The presented key, or <see langword="null"/> when none was.</param>
    /// <param name="Endpoint">The route the key was reserved against.</param>
    /// <param name="Replay">A result to return instead of running, when the key has been seen.</param>
    private sealed record BulkReservation(string? Key, string Endpoint, IResult? Replay)
    {
        /// <summary>A batch that carries no idempotency key.</summary>
        internal static BulkReservation None { get; } = new(null, string.Empty, null);

        /// <summary>Stores the summary of a finished batch.</summary>
        /// <param name="store">The replay store.</param>
        /// <param name="created">How many links were created.</param>
        /// <param name="failed">How many rows failed.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task that completes when the summary has been stored.</returns>
        internal async Task CompleteAsync(
            IdempotencyStore store,
            int created,
            int failed,
            CancellationToken cancellationToken)
        {
            if (Key is null)
            {
                return;
            }

            string summary = string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"created\":{created},\"failed\":{failed}}}");

            await store.CompleteAsync(
                Key,
                Endpoint,
                StatusCodes.Status200OK,
                summary,
                cancellationToken);
        }

        /// <summary>Releases the reservation of a batch that did not finish.</summary>
        /// <param name="store">The replay store.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task that completes when the reservation has been released.</returns>
        internal async Task AbandonAsync(IdempotencyStore store, CancellationToken cancellationToken)
        {
            if (Key is not null)
            {
                await store.AbandonAsync(Key, Endpoint, cancellationToken);
            }
        }
    }
}
