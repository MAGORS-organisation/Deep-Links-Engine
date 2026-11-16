using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dle.Persistence.Fast.Telemetry;

/// <summary>
/// Zero-dependency OpenMetrics / Prometheus text-exposition collector built directly on the BCL
/// <see cref="MeterListener"/> (no extra exporter packages required).
/// </summary>
/// <remarks>
/// <para>
/// Instruments matching the configured meter prefixes are recorded lock-free in memory and
/// rendered on scrape in Prometheus text format (<c>0.0.4</c>). Distinct tag-set cardinality per
/// instrument is capped to bound memory if a caller passes unexpected high-cardinality tags.
/// </para>
/// </remarks>
public sealed class PrometheusMetricsCollector : IDisposable
{
    /// <summary>Content type expected by Prometheus scrapers (<c>0.0.4</c>).</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    private const int MaxSeriesPerInstrument = 2048;

    private static readonly double[] DefaultSecondsBuckets =
        [0.005d, 0.01d, 0.025d, 0.05d, 0.1d, 0.25d, 0.5d, 1d, 2.5d, 5d, 10d];

    private static readonly double[] DefaultCountBuckets =
        [1d, 5d, 10d, 50d, 100d, 500d, 1000d, 5000d, 10000d];

    private readonly string[] _meterPrefixes;
    private readonly Dictionary<string, double[]> _customHistogramBuckets;
    private readonly ConcurrentDictionary<Instrument, InstrumentFamily> _families = new();
    private readonly MeterListener _listener;
    private readonly Lock _scrapeLock = new();
    private bool _disposed;

    /// <summary>
    /// Starts collecting measurements from instruments whose meter name matches any entry in
    /// <paramref name="meterPrefixes"/> (exact match or prefix followed by <c>'.'</c>).
    /// </summary>
    /// <param name="meterPrefixes">Meter names or prefixes to collect.</param>
    /// <param name="customHistogramBuckets">Optional explicit bucket boundaries keyed by instrument name.</param>
    public PrometheusMetricsCollector(
        IEnumerable<string> meterPrefixes,
        IReadOnlyDictionary<string, double[]>? customHistogramBuckets = null)
    {
        ArgumentNullException.ThrowIfNull(meterPrefixes);

        _meterPrefixes = meterPrefixes
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        _customHistogramBuckets = new Dictionary<string, double[]>(StringComparer.Ordinal);
        if (customHistogramBuckets is not null)
        {
            foreach (KeyValuePair<string, double[]> kvp in customHistogramBuckets)
            {
                if (kvp.Value is { Length: > 0 })
                {
                    _customHistogramBuckets[kvp.Key] = [.. kvp.Value.OrderBy(static b => b)];
                }
            }
        }

        _listener = new MeterListener
        {
            InstrumentPublished = OnInstrumentPublished,
            MeasurementsCompleted = OnMeasurementsCompleted,
        };

        _listener.SetMeasurementEventCallback<byte>((i, v, t, s) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<short>((i, v, t, s) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<int>((i, v, t, s) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<long>((i, v, t, s) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<float>((i, v, t, s) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<double>((i, v, t, s) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<decimal>((i, v, t, s) => Record(i, (double)v, t));

        _listener.Start();
    }

    /// <summary>
    /// Verifies an optional Prometheus <c>Authorization: Bearer &lt;token&gt;</c> header in
    /// constant time. Returns <see langword="true"/> when <paramref name="expectedToken"/> is null
    /// or empty (open internal cluster scrape).
    /// </summary>
    /// <param name="expectedToken">Configured scrape bearer token, or null/empty when disabled.</param>
    /// <param name="authorizationHeader">Incoming <c>Authorization</c> header value.</param>
    /// <returns><see langword="true"/> if scraping is allowed.</returns>
    public static bool IsAuthorizedScrape(string? expectedToken, string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(expectedToken))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(authorizationHeader)
            || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> presented = authorizationHeader.AsSpan("Bearer ".Length).Trim();
        byte[] expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken));
        byte[] actualDigest = SHA256.HashData(Encoding.UTF8.GetBytes(presented.ToString()));
        return CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest);
    }

    /// <summary>
    /// Polls observable instruments and writes all collected metrics in Prometheus text exposition
    /// format (<c>0.0.4</c>).
    /// </summary>
    /// <returns>The UTF-8 text document.</returns>
    public string Scrape()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sb = new StringBuilder(4096);

        lock (_scrapeLock)
        {
            foreach (InstrumentFamily family in _families.Values)
            {
                if (family.IsObservable)
                {
                    family.BeginObservableCycle();
                }
            }

            _listener.RecordObservableInstruments();

            foreach (InstrumentFamily family in _families.Values
                .OrderBy(static f => f.PrometheusName, StringComparer.Ordinal))
            {
                family.WritePrometheus(sb);
            }
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listener.Dispose();
        _families.Clear();
    }

    private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
    {
        if (!MatchesMeter(instrument.Meter.Name))
        {
            return;
        }

        InstrumentFamily family = CreateFamily(instrument);
        _families.TryAdd(instrument, family);
        listener.EnableMeasurementEvents(instrument, family);
    }

    private void OnMeasurementsCompleted(Instrument instrument, object? state)
    {
        _families.TryRemove(instrument, out _);
    }

    private bool MatchesMeter(string meterName)
    {
        foreach (string prefix in _meterPrefixes)
        {
            if (meterName.Equals(prefix, StringComparison.Ordinal)
                || (prefix.EndsWith('.') && meterName.StartsWith(prefix, StringComparison.Ordinal))
                || meterName.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private InstrumentFamily CreateFamily(Instrument instrument)
    {
        Type def = instrument.GetType().IsGenericType
            ? instrument.GetType().GetGenericTypeDefinition()
            : instrument.GetType();

        MetricKind kind;
        bool isObservable = false;

        if (def == typeof(Counter<>))
        {
            kind = MetricKind.Counter;
        }
        else if (def == typeof(ObservableCounter<>))
        {
            kind = MetricKind.Counter;
            isObservable = true;
        }
        else if (def == typeof(Histogram<>))
        {
            kind = MetricKind.Histogram;
        }
        else if (def == typeof(ObservableGauge<>) || def == typeof(ObservableUpDownCounter<>))
        {
            kind = MetricKind.Gauge;
            isObservable = true;
        }
        else
        {
            kind = MetricKind.Gauge;
        }

        string promName = SanitizeMetricName(instrument.Name, kind == MetricKind.Counter);
        double[]? buckets = null;
        if (kind == MetricKind.Histogram)
        {
            if (_customHistogramBuckets.TryGetValue(instrument.Name, out double[]? explicitBuckets))
            {
                buckets = explicitBuckets;
            }
            else
            {
                buckets = string.Equals(instrument.Unit, "s", StringComparison.Ordinal)
                    ? DefaultSecondsBuckets
                    : DefaultCountBuckets;
            }
        }

        return new InstrumentFamily(promName, instrument.Description ?? string.Empty, kind, isObservable, buckets);
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (_disposed || double.IsNaN(value) || !_families.TryGetValue(instrument, out InstrumentFamily? family))
        {
            return;
        }

        family.Record(value, tags);
    }

    private static string SanitizeMetricName(string name, bool isCounter)
    {
        Span<char> buffer = name.Length <= 128 ? stackalloc char[name.Length] : new char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool valid = (c is >= 'a' and <= 'z')
                || (c is >= 'A' and <= 'Z')
                || (c is >= '0' and <= '9' && i > 0)
                || c is '_' or ':';

            buffer[i] = valid ? c : '_';
        }

        string sanitized = new(buffer);
        if (sanitized.Length == 0 || (sanitized[0] is >= '0' and <= '9'))
        {
            sanitized = "_" + sanitized;
        }

        if (isCounter && !sanitized.EndsWith("_total", StringComparison.Ordinal))
        {
            sanitized += "_total";
        }

        return sanitized;
    }

    private enum MetricKind
    {
        Counter,
        Gauge,
        Histogram,
    }

    private sealed class InstrumentFamily(
        string prometheusName,
        string description,
        MetricKind kind,
        bool isObservable,
        double[]? buckets)
    {
        private readonly ConcurrentDictionary<string, SeriesState> _series = new(StringComparer.Ordinal);
        private int _seriesCount;

        public string PrometheusName { get; } = prometheusName;
        public string Description { get; } = description;
        public MetricKind Kind { get; } = kind;
        public bool IsObservable { get; } = isObservable;
        public double[]? Buckets { get; } = buckets;

        public void BeginObservableCycle()
        {
            foreach (SeriesState state in _series.Values)
            {
                state.ResetObservedFlag();
            }
        }

        public void Record(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string labelKey = FormatLabels(tags);

            if (!_series.TryGetValue(labelKey, out SeriesState? state))
            {
                if (Volatile.Read(ref _seriesCount) >= MaxSeriesPerInstrument)
                {
                    return;
                }

                SeriesState candidate = new(Buckets);
                if (_series.TryAdd(labelKey, candidate))
                {
                    Interlocked.Increment(ref _seriesCount);
                    state = candidate;
                }
                else if (!_series.TryGetValue(labelKey, out state))
                {
                    return;
                }
            }

            if (Kind == MetricKind.Histogram)
            {
                state.ObserveHistogram(value, Buckets!);
            }
            else if (IsObservable)
            {
                state.SetObservable(value);
            }
            else
            {
                state.Add(value);
            }
        }

        public void WritePrometheus(StringBuilder sb)
        {
            if (_series.IsEmpty)
            {
                return;
            }

            if (!string.IsNullOrEmpty(Description))
            {
                sb.Append("# HELP ").Append(PrometheusName).Append(' ');
                AppendEscapedHelp(sb, Description);
                sb.Append('\n');
            }

            sb.Append("# TYPE ").Append(PrometheusName).Append(' ').Append(Kind switch
            {
                MetricKind.Counter => "counter",
                MetricKind.Histogram => "histogram",
                _ => "gauge",
            }).Append('\n');

            foreach (KeyValuePair<string, SeriesState> entry in _series.OrderBy(static k => k.Key, StringComparer.Ordinal))
            {
                if (IsObservable && !entry.Value.WasObservedThisCycle)
                {
                    continue;
                }

                if (Kind == MetricKind.Histogram)
                {
                    entry.Value.WriteHistogram(sb, PrometheusName, entry.Key, Buckets!);
                }
                else
                {
                    sb.Append(PrometheusName);
                    if (entry.Key.Length > 0)
                    {
                        sb.Append('{').Append(entry.Key).Append('}');
                    }

                    sb.Append(' ').Append(FormatDouble(entry.Value.ReadValue())).Append('\n');
                }
            }
        }
    }

    private sealed class SeriesState
    {
        private readonly long[]? _bucketCounts;
        private long _sumBits;
        private int _observed;

        public SeriesState(double[]? buckets)
        {
            if (buckets is not null)
            {
                _bucketCounts = new long[buckets.Length + 1];
            }
        }

        public bool WasObservedThisCycle => Volatile.Read(ref _observed) != 0;

        public void ResetObservedFlag() => Volatile.Write(ref _observed, 0);

        public void SetObservable(double value)
        {
            Volatile.Write(ref _sumBits, BitConverter.DoubleToInt64Bits(value));
            Volatile.Write(ref _observed, 1);
        }

        public void Add(double delta)
        {
            while (true)
            {
                long currentBits = Volatile.Read(ref _sumBits);
                double current = BitConverter.Int64BitsToDouble(currentBits);
                long nextBits = BitConverter.DoubleToInt64Bits(current + delta);
                if (Interlocked.CompareExchange(ref _sumBits, nextBits, currentBits) == currentBits)
                {
                    return;
                }
            }
        }

        public void ObserveHistogram(double value, double[] buckets)
        {
            int idx = buckets.Length;
            for (int i = 0; i < buckets.Length; i++)
            {
                if (value <= buckets[i])
                {
                    idx = i;
                    break;
                }
            }

            Interlocked.Increment(ref _bucketCounts![idx]);
            Add(value);
        }

        public double ReadValue() => BitConverter.Int64BitsToDouble(Volatile.Read(ref _sumBits));

        public void WriteHistogram(StringBuilder sb, string name, string labels, double[] buckets)
        {
            long cumulative = 0;
            for (int i = 0; i < buckets.Length; i++)
            {
                cumulative += Interlocked.Read(ref _bucketCounts![i]);
                AppendHistogramLine(sb, name, "_bucket", labels, "le", FormatDouble(buckets[i]), cumulative);
            }

            cumulative += Interlocked.Read(ref _bucketCounts![buckets.Length]);
            AppendHistogramLine(sb, name, "_bucket", labels, "le", "+Inf", cumulative);

            sb.Append(name).Append("_sum");
            if (labels.Length > 0)
            {
                sb.Append('{').Append(labels).Append('}');
            }

            sb.Append(' ').Append(FormatDouble(ReadValue())).Append('\n');

            sb.Append(name).Append("_count");
            if (labels.Length > 0)
            {
                sb.Append('{').Append(labels).Append('}');
            }

            sb.Append(' ').Append(cumulative.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        private static void AppendHistogramLine(
            StringBuilder sb,
            string name,
            string suffix,
            string labels,
            string extraKey,
            string extraValue,
            long count)
        {
            sb.Append(name).Append(suffix).Append('{');
            if (labels.Length > 0)
            {
                sb.Append(labels).Append(',');
            }

            sb.Append(extraKey).Append("=\"").Append(extraValue).Append("\"} ")
                .Append(count.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }
    }

    private static string FormatLabels(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.IsEmpty)
        {
            return string.Empty;
        }

        (string Key, string Value)[] normalized = new (string, string)[tags.Length];
        int count = 0;

        for (int i = 0; i < tags.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(tags[i].Key))
            {
                continue;
            }

            string k = SanitizeLabelName(tags[i].Key);
            string v = tags[i].Value switch
            {
                null => string.Empty,
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
                object o => o.ToString() ?? string.Empty,
            };
            normalized[count++] = (k, v);
        }

        if (count == 0)
        {
            return string.Empty;
        }

        if (count > 1)
        {
            Array.Sort(normalized, 0, count, Comparer<(string Key, string Value)>.Create(
                static (a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal)));
        }

        var sb = new StringBuilder(64);
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(normalized[i].Key).Append("=\"");
            AppendEscapedLabelValue(sb, normalized[i].Value);
            sb.Append('"');
        }

        return sb.ToString();
    }

    private static string SanitizeLabelName(string name)
    {
        Span<char> buffer = name.Length <= 64 ? stackalloc char[name.Length] : new char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool valid = (c is >= 'a' and <= 'z')
                || (c is >= 'A' and <= 'Z')
                || (c is >= '0' and <= '9' && i > 0)
                || c == '_';
            buffer[i] = valid ? c : '_';
        }

        string s = new(buffer);
        if (s.StartsWith("__", StringComparison.Ordinal) || (s.Length > 0 && s[0] is >= '0' and <= '9'))
        {
            s = "_" + s;
        }

        return s;
    }

    private static void AppendEscapedLabelValue(StringBuilder sb, string value)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
    }

    private static void AppendEscapedHelp(StringBuilder sb, string value)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
    }

    private static string FormatDouble(double value)
    {
        if (double.IsPositiveInfinity(value))
        {
            return "+Inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Inf";
        }

        return value.ToString("G", CultureInfo.InvariantCulture);
    }
}
