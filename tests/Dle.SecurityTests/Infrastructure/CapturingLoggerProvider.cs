using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Dle.SecurityTests.Infrastructure;

/// <summary>One line the host logged, flattened to the text a sink would ship.</summary>
/// <param name="Category">Logger category.</param>
/// <param name="Level">Severity.</param>
/// <param name="Message">Formatted message.</param>
/// <param name="State">Every structured value, rendered.</param>
public sealed record CapturedLog(string Category, LogLevel Level, string Message, string State)
{
    /// <summary>Everything a log sink would receive for this entry, as one string.</summary>
    public string Everything => Message + " " + State;
}

/// <summary>
/// Captures everything the host logs so that S-08 can be asserted rather than reviewed.
/// </summary>
/// <remarks>
/// <para>
/// T-08 is a GDPR incident waiting in a log file: the address, the user agent and the full URL of every
/// click. §E.2.2 forbids logging the <c>Authorization</c> header, the referrer and the query string at
/// <c>Information</c>, and S-08 makes "no PII at Information or below" an acceptance criterion.
/// </para>
/// <para>
/// An acceptance criterion phrased that way is only checkable against a real capture: it is about what
/// the process emits, including from framework components nobody in this repository wrote. So the
/// provider is installed into the running host and the assertions read what came out, rather than
/// inspecting the call sites of a scrubber that a future middleware may bypass.
/// </para>
/// </remarks>
[ProviderAlias("Capturing")]
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    /// <summary>Everything captured since the last <see cref="Clear"/>.</summary>
    public IReadOnlyCollection<CapturedLog> Entries => _entries;

    /// <summary>Entries at <see cref="LogLevel.Information"/> or below, which is S-08's scope.</summary>
    public IEnumerable<CapturedLog> AtInformationOrBelow =>
        _entries.Where(entry => entry.Level <= LogLevel.Information);

    /// <summary>Forgets everything captured so far.</summary>
    public void Clear() => _entries.Clear();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    /// <inheritdoc />
    public void Dispose() => _entries.Clear();

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            string rendered = state is IReadOnlyList<KeyValuePair<string, object?>> values
                ? string.Join(
                    " ",
                    values.Select(pair => pair.Key + "=" + Convert.ToString(pair.Value, CultureInfo.InvariantCulture)))
                : Convert.ToString(state, CultureInfo.InvariantCulture) ?? string.Empty;

            entries.Enqueue(new CapturedLog(
                category,
                logLevel,
                formatter(state, exception) + " " + (exception?.ToString() ?? string.Empty),
                rendered));
        }
    }
}
