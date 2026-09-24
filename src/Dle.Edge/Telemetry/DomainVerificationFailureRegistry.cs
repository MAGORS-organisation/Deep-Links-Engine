using System.Collections.Concurrent;

namespace Dle.Edge.Telemetry;

/// <summary>
/// Hosts whose Apple or Android association file currently fails to build or validate, behind the
/// <c>dle_domain_verification_failures</c> gauge of §C.6.
/// </summary>
/// <remarks>
/// <para>
/// A broken association file is invisible from the outside: universal links silently stop opening
/// the application and every click quietly degrades to the web fallback, which looks like a drop in
/// conversion rather than an incident. §C.6 therefore puts an alert on this gauge, and the gauge
/// needs something to count.
/// </para>
/// <para>
/// The registry is a set rather than a counter so that a host failing on every request is counted
/// once, and so that a host that starts working again removes itself. It is written by whatever
/// serves <c>/.well-known/*</c> and read only by the metric.
/// </para>
/// </remarks>
public sealed class DomainVerificationFailureRegistry
{
    private readonly ConcurrentDictionary<string, byte> _failing =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of hosts currently in a failed state.</summary>
    public int Count => _failing.Count;

    /// <summary>
    /// Records the current verification state of one host.
    /// </summary>
    /// <param name="host">Normalized host.</param>
    /// <param name="failed"><see langword="true"/> when the association file is broken or missing.</param>
    /// <exception cref="ArgumentException"><paramref name="host"/> is null or blank.</exception>
    public void Report(string host, bool failed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (failed)
        {
            _failing[host] = 0;
        }
        else
        {
            _ = _failing.TryRemove(host, out _);
        }
    }

    /// <summary>Whether a host is currently recorded as failing.</summary>
    /// <param name="host">Normalized host.</param>
    /// <returns><see langword="true"/> when the host is in a failed state.</returns>
    public bool IsFailing(string host) => host is not null && _failing.ContainsKey(host);
}
