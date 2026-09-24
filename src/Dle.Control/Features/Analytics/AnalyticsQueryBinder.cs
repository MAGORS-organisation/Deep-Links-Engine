using Dle.Analytics.Postgres;
using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Domain.Analytics;

using Microsoft.AspNetCore.Http;

namespace Dle.Control.Features.Analytics;

/// <summary>
/// A bound analytics query, or the problem document explaining why there is none.
/// </summary>
/// <param name="Query">The query, scoped to the caller's tenant.</param>
/// <param name="Problem">The refusal.</param>
public readonly record struct AnalyticsQueryBinding(AnalyticsQuery? Query, IResult? Problem);

/// <summary>
/// Turns the query string of a reporting request into an <see cref="AnalyticsQuery"/> (FR-202,
/// FR-205).
/// </summary>
/// <remarks>
/// <para>
/// Two properties are enforced here rather than in each handler, because each handler is one more
/// chance to forget them.
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <b>The tenant comes from the credential, never from the query string.</b> There is no
///     <c>tenant_id</c> parameter and there will not be one. A report that could name its own
///     tenant would be a cross-tenant read behind a viewer role.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Bots are excluded unless explicitly asked for.</b> FR-205 is blunt about why: a crawler's
///     click is a real row and belongs in the stream, but counting it in a campaign inflates every
///     number a marketer makes a decision on. The default is therefore off, and turning it on is a
///     conscious <c>include_bots=true</c> (TC-106).
///     </description>
///   </item>
/// </list>
/// <para>
/// Every bound is validated up front: an unparseable instant, an inverted window or an unknown
/// grain is a 400 with a message, never a query that silently reports something else.
/// </para>
/// </remarks>
public static class AnalyticsQueryBinder
{
    /// <summary>Widest window a single report may cover, in days.</summary>
    /// <remarks>
    /// Two years, matching the default aggregate retention. Beyond that there is nothing to report
    /// on, and an unbounded window is how one caller turns a reporting endpoint into an outage.
    /// </remarks>
    public const int MaxWindowDays = 731;

    /// <summary>Default window applied when the caller supplies neither bound.</summary>
    public const int DefaultWindowDays = 30;

    /// <summary>
    /// Binds a query.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="timeProvider">Clock, used for the default window (SHARED-KERNEL §17.2).</param>
    /// <param name="maxLimit">Largest row count the store will accept.</param>
    /// <returns>The query, or the problem document.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static AnalyticsQueryBinding Bind(HttpContext context, TimeProvider timeProvider, int maxLimit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeProvider);

        DleCaller caller = context.RequireDleCaller();
        IQueryCollection query = context.Request.Query;

        Dictionary<string, string[]> errors = new(StringComparer.Ordinal);
        DateTimeOffset now = timeProvider.GetUtcNow();

        DateTimeOffset to = ReadInstant(query, "to", now, errors);
        DateTimeOffset from = ReadInstant(query, "from", to.AddDays(-DefaultWindowDays), errors);

        if (from >= to)
        {
            errors["from"] = ["The window must start before it ends."];
        }
        else if ((to - from).TotalDays > MaxWindowDays)
        {
            errors["from"] =
            [
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A report may cover at most {MaxWindowDays} days."),
            ];
        }

        TimeGrain grain = ReadGrain(query, errors);
        long? linkId = ReadLinkId(query, errors);
        Guid? campaignId = ReadGuid(query, "campaign_id", errors);
        int limit = ReadLimit(query, maxLimit, errors);

        if (errors.Count > 0)
        {
            return new AnalyticsQueryBinding(
                null,
                DleProblemResults.ValidationFailed("The report parameters are not valid.", errors));
        }

        return new AnalyticsQueryBinding(
            new AnalyticsQuery
            {
                TenantId = caller.TenantId,
                From = from,
                To = to,
                Grain = grain,
                LinkId = linkId,
                CampaignId = campaignId,
                Country = Trimmed(query["country"]),
                Platform = Trimmed(query["platform"]),

                // FR-205, TC-106: off unless the caller says otherwise, in as many words.
                IncludeBots = ReadBoolean(query, "include_bots"),
                Limit = limit,
            },
            null);
    }

    /// <summary>Reads an ISO 8601 instant, falling back to a default.</summary>
    private static DateTimeOffset ReadInstant(
        IQueryCollection query,
        string name,
        DateTimeOffset fallback,
        Dictionary<string, string[]> errors)
    {
        string? raw = Trimmed(query[name]);

        if (raw is null)
        {
            return fallback;
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            errors[name] = ["The instant must be ISO 8601, for example 2026-09-01T00:00:00Z."];
            return fallback;
        }

        return parsed.ToUniversalTime();
    }

    /// <summary>Reads the bucket size.</summary>
    private static TimeGrain ReadGrain(IQueryCollection query, Dictionary<string, string[]> errors)
    {
        string? raw = Trimmed(query["grain"]);

        if (raw is null)
        {
            return TimeGrain.Day;
        }

        return raw.ToLowerInvariant() switch
        {
            "hour" => TimeGrain.Hour,
            "day" => TimeGrain.Day,
            "week" => TimeGrain.Week,
            "month" => TimeGrain.Month,
            _ => Reject(errors),
        };

        static TimeGrain Reject(Dictionary<string, string[]> errors)
        {
            errors["grain"] = ["The grain must be hour, day, week or month."];
            return TimeGrain.Day;
        }
    }

    /// <summary>Reads the link identifier, which is a 64 bit value carried as a string.</summary>
    private static long? ReadLinkId(IQueryCollection query, Dictionary<string, string[]> errors)
    {
        string? raw = Trimmed(query["link_id"]);

        if (raw is null)
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            errors["link_id"] = ["The link identifier must be a 64 bit integer in decimal."];
            return null;
        }

        return parsed;
    }

    /// <summary>Reads an identifier parameter.</summary>
    private static Guid? ReadGuid(IQueryCollection query, string name, Dictionary<string, string[]> errors)
    {
        string? raw = Trimmed(query[name]);

        if (raw is null)
        {
            return null;
        }

        if (!Guid.TryParse(raw, CultureInfo.InvariantCulture, out Guid parsed))
        {
            errors[name] = ["The identifier must be a UUID."];
            return null;
        }

        return parsed;
    }

    /// <summary>Reads the row limit, clamped to what the store will serve.</summary>
    private static int ReadLimit(IQueryCollection query, int maxLimit, Dictionary<string, string[]> errors)
    {
        string? raw = Trimmed(query["limit"]);

        if (raw is null)
        {
            return Math.Min(500, maxLimit);
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
        {
            errors["limit"] = ["The limit must be a positive integer."];
            return Math.Min(500, maxLimit);
        }

        return Math.Min(parsed, maxLimit);
    }

    /// <summary>Reads a boolean flag, treating anything but a clear yes as no.</summary>
    private static bool ReadBoolean(IQueryCollection query, string name)
    {
        string? raw = Trimmed(query[name]);

        return raw is not null
            && (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "1", StringComparison.Ordinal));
    }

    /// <summary>Reads one query value, mapping blank to absent.</summary>
    private static string? Trimmed(Microsoft.Extensions.Primitives.StringValues values)
    {
        string? value = values.Count == 0 ? null : values[0];

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Resolves the requested breakdown dimension against the allowlist.</summary>
    /// <param name="dimension">The requested dimension.</param>
    /// <param name="resolved">The canonical dimension name.</param>
    /// <param name="problem">The problem document when the name is unknown.</param>
    /// <returns><see langword="true"/> when the dimension may be grouped by.</returns>
    /// <remarks>
    /// The allowlist lives in the analytics module because it is the module that maps a name onto a
    /// SQL expression. Checking it here means an unknown dimension is a 400 with the list of valid
    /// ones rather than a 500 from a query that could not be built (CA2100, §17).
    /// </remarks>
    public static bool TryResolveDimension(string? dimension, out string resolved, out IResult? problem)
    {
        if (AnalyticsDimensions.TryResolve(dimension, out AnalyticsDimension? found))
        {
            resolved = found.Name;
            problem = null;
            return true;
        }

        resolved = string.Empty;

        problem = DleProblemResults.ValidationFailed(
            "The requested breakdown dimension is not supported.",
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["dimension"] =
                [
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Supported dimensions: {string.Join(", ", AnalyticsDimensions.Names)}."),
                ],
            });

        return false;
    }
}
