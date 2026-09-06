using System.Diagnostics;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// Waits for an effect that a background loop produces.
/// </summary>
/// <remarks>
/// <para>
/// Used for exactly one thing: the click stream. A resolve returns before its click event is
/// written, on purpose — FR-165 says the response must never wait on telemetry — so a test that
/// asserts on the row has to wait for the batch writer's flush interval to elapse.
/// </para>
/// <para>
/// This is not a wall-clock dependency in the sense SHARED-KERNEL §17.2 forbids. Nothing under test
/// reads a clock here and no assertion depends on how long the wait took; the deadline exists only
/// so that a hung background loop fails with a readable message instead of hanging the run.
/// </para>
/// </remarks>
public static class Eventually
{
    /// <summary>Default deadline: an order of magnitude above the 250 ms flush interval of §C.3.2.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How often the condition is re-evaluated.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Polls until the condition holds, or fails the test.
    /// </summary>
    /// <param name="condition">The condition, re-evaluated until it holds.</param>
    /// <param name="because">What was being waited for, quoted in the failure.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="timeout">Deadline; <see cref="DefaultTimeout"/> when omitted.</param>
    /// <returns>A task that completes when the condition holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is <see langword="null"/>.</exception>
    public static async Task TrueAsync(
        Func<Task<bool>> condition,
        string because,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        TimeSpan deadline = timeout ?? DefaultTimeout;
        long started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail(
            "Timed out after " + deadline.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
            + " s waiting for: " + because);
    }
}
