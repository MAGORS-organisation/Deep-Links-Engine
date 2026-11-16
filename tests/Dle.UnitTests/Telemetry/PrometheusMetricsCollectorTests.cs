using System.Diagnostics.Metrics;

using Dle.Persistence.Fast.Telemetry;

using Xunit;

namespace Dle.UnitTests.Telemetry;

public sealed class PrometheusMetricsCollectorTests
{
    [Fact]
    public void Scrape_RendersCountersGaugesObservablesAndHistograms()
    {
        string meterName = $"Dle.Test.{Guid.NewGuid():N}";
        using var meter = new Meter(meterName);
        long observableValue = 7;

        using var collector = new PrometheusMetricsCollector(
            [meterName],
            new Dictionary<string, double[]>(StringComparer.Ordinal)
            {
                ["dle.resolve.duration"] = [0.005d, 0.025d, 0.1d],
            });

        Counter<long> counter = meter.CreateCounter<long>("dle.resolve", description: "Resolve total");
        UpDownCounter<int> upDown = meter.CreateUpDownCounter<int>("dle.inflight");
        Histogram<double> histogram = meter.CreateHistogram<double>("dle.resolve.duration", unit: "s");
        _ = meter.CreateObservableGauge("dle.cache.ratio", () => observableValue, unit: "1");

        counter.Add(2, new KeyValuePair<string, object?>("outcome", "redirect"), new KeyValuePair<string, object?>("cache", "l1"));
        counter.Add(1, new KeyValuePair<string, object?>("cache", "l1"), new KeyValuePair<string, object?>("outcome", "redirect"));
        upDown.Add(3);
        upDown.Add(-1);
        histogram.Record(0.003d, new KeyValuePair<string, object?>("cache", "l1"));
        histogram.Record(0.05d, new KeyValuePair<string, object?>("cache", "l1"));

        string text = collector.Scrape();

        Assert.Contains("# TYPE dle_resolve_total counter", text, StringComparison.Ordinal);
        Assert.Contains("dle_resolve_total{cache=\"l1\",outcome=\"redirect\"} 3", text, StringComparison.Ordinal);
        Assert.Contains("# TYPE dle_inflight gauge", text, StringComparison.Ordinal);
        Assert.Contains("dle_inflight 2", text, StringComparison.Ordinal);
        Assert.Contains("# TYPE dle_cache_ratio gauge", text, StringComparison.Ordinal);
        Assert.Contains("dle_cache_ratio 7", text, StringComparison.Ordinal);
        Assert.Contains("dle_resolve_duration_bucket{cache=\"l1\",le=\"0.005\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("dle_resolve_duration_bucket{cache=\"l1\",le=\"0.025\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("dle_resolve_duration_bucket{cache=\"l1\",le=\"0.1\"} 2", text, StringComparison.Ordinal);
        Assert.Contains("dle_resolve_duration_bucket{cache=\"l1\",le=\"+Inf\"} 2", text, StringComparison.Ordinal);
        Assert.Contains("dle_resolve_duration_count{cache=\"l1\"} 2", text, StringComparison.Ordinal);
        Assert.Contains("dle_resolve_duration_sum{cache=\"l1\"} 0.053", text, StringComparison.Ordinal);
    }

    [Fact]
    public void IsAuthorizedScrape_ValidatesBearerTokenInConstantTime()
    {
        Assert.True(PrometheusMetricsCollector.IsAuthorizedScrape(null, null));
        Assert.True(PrometheusMetricsCollector.IsAuthorizedScrape("", "Bearer anything"));
        Assert.True(PrometheusMetricsCollector.IsAuthorizedScrape("secret-token", "Bearer secret-token"));
        Assert.False(PrometheusMetricsCollector.IsAuthorizedScrape("secret-token", null));
        Assert.False(PrometheusMetricsCollector.IsAuthorizedScrape("secret-token", "Basic dXNlcjpwYXNz"));
        Assert.False(PrometheusMetricsCollector.IsAuthorizedScrape("secret-token", "Bearer wrong-token"));
    }
}
