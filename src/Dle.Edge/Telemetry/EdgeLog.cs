using Microsoft.Extensions.Logging;

namespace Dle.Edge.Telemetry;

/// <summary>
/// Source-generated log messages for the resolve path (§C.2).
/// </summary>
/// <remarks>
/// <para>
/// Every message on the hot path goes through <c>[LoggerMessage]</c>. The generator emits a cached
/// delegate and an <c>IsEnabled</c> guard per message, so a disabled <c>Debug</c> call costs a
/// branch and boxes nothing; the same call written as <c>logger.LogDebug($"…")</c> would allocate an
/// argument array and format a string on every request whether or not anybody is listening.
/// </para>
/// <para>
/// What is <em>not</em> here matters as much as what is. SHARED-KERNEL §17.5 forbids logging the
/// <c>Authorization</c> header, the full referrer, the full query string or a raw client address at
/// <c>Information</c> or below, so no parameter below carries any of them: the referrer appears as a
/// host, the address as a /24 or /48 network prefix, and the query string never appears at all.
/// </para>
/// </remarks>
public static partial class EdgeLog
{
    /// <summary>Logs a completed resolve.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="host">Normalized host.</param>
    /// <param name="slug">Normalized slug.</param>
    /// <param name="decision">Click stream decision name.</param>
    /// <param name="cache">Cache level that answered the lookup.</param>
    /// <param name="elapsedMs">Elapsed milliseconds.</param>
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Debug,
        Message = "Resolved {Host}/{Slug} as {Decision} from {Cache} in {ElapsedMs} ms.")]
    public static partial void Resolved(ILogger logger, string host, string slug, string decision, string cache, double elapsedMs);

    /// <summary>Logs a request that produced no link.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="host">Normalized host.</param>
    /// <param name="reason">Why nothing was served, one of the reasons in <c>NotFoundReasons</c>.</param>
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Debug,
        Message = "No link served for host {Host}: {Reason}.")]
    public static partial void NotServed(ILogger logger, string host, string reason);

    /// <summary>Logs that the link lookup failed because a dependency was unavailable.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="host">Normalized host.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Link lookup failed for host {Host}; answering 503 rather than 500.")]
    public static partial void LookupFailed(ILogger logger, string host, Exception exception);

    /// <summary>Logs a user agent that claimed to be a crawler and failed reverse DNS confirmation.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="crawler">The crawler the user agent claimed to be.</param>
    /// <param name="networkPrefix">The /24 or /48 network prefix the claim came from.</param>
    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "User agent claimed to be {Crawler} but reverse DNS did not confirm it; treating {NetworkPrefix} as an ordinary client.")]
    public static partial void SpoofedBot(ILogger logger, string crawler, string networkPrefix);

    /// <summary>Logs that a network prefix entered the enumeration shadow ban.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="networkPrefix">The banned /24 or /48 network prefix.</param>
    /// <param name="minutes">Ban duration in minutes.</param>
    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "Network prefix {NetworkPrefix} exhausted its 404 budget and is shadow banned for {Minutes} minutes.")]
    public static partial void ShadowBanned(ILogger logger, string networkPrefix, int minutes);

    /// <summary>Logs that a click event could not be queued and was dropped.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="dropped">Total events dropped by this process so far.</param>
    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Warning,
        Message = "Click event dropped because the bounded channel is full; {Dropped} dropped so far. The redirect was still served (FR-165).")]
    public static partial void ClickEventDropped(ILogger logger, long dropped);

    /// <summary>Logs a successful load of the geographic database.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="path">Database file path.</param>
    /// <param name="databaseType">Database type reported by the file metadata.</param>
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Information,
        Message = "Geographic database {Path} loaded in memory-mapped mode ({DatabaseType}).")]
    public static partial void GeoIpLoaded(ILogger logger, string path, string databaseType);

    /// <summary>Logs that the geographic database is missing or unreadable.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="path">Database file path.</param>
    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Warning,
        Message = "Geographic database {Path} is missing or unreadable; country stays null and geo-dependent rules fall through to the default rule (§D.6).")]
    public static partial void GeoIpUnavailable(ILogger logger, string path);

    /// <summary>Logs that the geographic database could not be opened.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="path">Database file path.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Error,
        Message = "Geographic database {Path} could not be opened; continuing without geographic data.")]
    public static partial void GeoIpLoadFailed(ILogger logger, string path, Exception exception);

    /// <summary>Logs that geographic lookup is switched off by configuration.</summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(
        EventId = 1013,
        Level = LogLevel.Information,
        Message = "Geographic lookup is disabled by configuration; country stays null for every request.")]
    public static partial void GeoIpDisabled(ILogger logger);

    /// <summary>Logs that a replaced geographic database file was swapped in.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="path">Database file path.</param>
    [LoggerMessage(
        EventId = 1014,
        Level = LogLevel.Information,
        Message = "Geographic database {Path} changed on disk and was reloaded.")]
    public static partial void GeoIpReloaded(ILogger logger, string path);

    /// <summary>Logs a failed reverse DNS confirmation attempt.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="crawler">The crawler the user agent claimed to be.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Debug,
        Message = "Reverse DNS confirmation for {Crawler} did not complete; the claim is treated as unconfirmed.")]
    public static partial void BotVerificationFailed(ILogger logger, string crawler, Exception exception);

    /// <summary>Logs that the interstitial renderer in use is the built-in fallback.</summary>
    /// <param name="logger">The logger.</param>
    [LoggerMessage(
        EventId = 1030,
        Level = LogLevel.Information,
        Message = "No branded renderer is registered; the built-in accessible fallback pages are in use.")]
    public static partial void FallbackRendererInUse(ILogger logger);

    /// <summary>Logs that an association document could not be built because a dependency failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="document">Which association file was requested.</param>
    /// <param name="host">Normalized host.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(
        EventId = 1031,
        Level = LogLevel.Error,
        Message = "The {Document} document for host {Host} could not be built; answering 503 rather than 500 or 404.")]
    public static partial void WellKnownUnavailable(ILogger logger, string document, string host, Exception exception);
}
