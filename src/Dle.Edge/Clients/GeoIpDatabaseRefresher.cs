using System.Diagnostics.CodeAnalysis;

using Dle.Edge.Configuration;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Edge.Clients;

/// <summary>
/// Background job that notices a replaced GeoLite2 file and asks
/// <see cref="MaxMindGeoIpResolver"/> to swap the reader (§B.6.1, §D.6).
/// </summary>
/// <remarks>
/// <para>
/// It watches, it does not download. NFR-14 forbids an outbound third-party call from anything the
/// resolve path depends on, and the database is exactly that; fetching a new edition belongs to the
/// deployment — a <c>geoipupdate</c> sidecar, a cron job, a re-mounted volume — and this loop's whole
/// job is to make the process pick the new file up without a restart.
/// </para>
/// <para>
/// A file system watcher would react faster and would also fire several times while a multi-hundred
/// megabyte file is being written, which is how a reader ends up opened on a half-written database. A
/// timer that compares length and last-write time sees the file only after the writer has finished
/// with it, and a refresh interval measured in tens of minutes is the right resolution for data that
/// is published twice a week.
/// </para>
/// </remarks>
public sealed partial class GeoIpDatabaseRefresher : BackgroundService
{
    private readonly MaxMindGeoIpResolver _resolver;
    private readonly GeoIpOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GeoIpDatabaseRefresher> _logger;

    /// <summary>Creates the refresher.</summary>
    /// <param name="resolver">The resolver whose reader is swapped.</param>
    /// <param name="options">Geographic options; supply the refresh interval.</param>
    /// <param name="timeProvider">Clock driving the timer (SHARED-KERNEL §17.2).</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public GeoIpDatabaseRefresher(
        MaxMindGeoIpResolver resolver,
        IOptions<GeoIpOptions> options,
        TimeProvider timeProvider,
        ILogger<GeoIpDatabaseRefresher> logger)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _resolver = resolver;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A background loop that dies takes the refresh with it for the life of the process. Every " +
                        "failure is logged and the loop continues with the database it already has, which is the " +
                        "explicit fallback SHARED-KERNEL §17.9 asks for.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AutoUpdate)
        {
            AutoUpdateDisabled(_logger);
            return;
        }

        var period = TimeSpan.FromMinutes(_options.RefreshMinutes);

        using var timer = new PeriodicTimer(period, _timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }

                _ = _resolver.Reload();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                RefreshFailed(_logger, exception);
            }
        }
    }

    [LoggerMessage(
        EventId = 1016,
        Level = LogLevel.Information,
        Message = "Automatic reload of the geographic database is disabled; the file loaded at start-up is used for the life of the process.")]
    private static partial void AutoUpdateDisabled(ILogger logger);

    [LoggerMessage(
        EventId = 1017,
        Level = LogLevel.Warning,
        Message = "The geographic database refresh tick failed; the previously loaded database keeps serving.")]
    private static partial void RefreshFailed(ILogger logger, Exception exception);
}
