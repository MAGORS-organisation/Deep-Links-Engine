namespace Dle.UnitTests.Crypto;

/// <summary>
/// A clock whose value the test sets. SHARED-KERNEL §17.2 forbids reading the machine clock in
/// engine code, and a test that depends on wall-clock time is a defect for the same reason: it
/// cannot be replayed.
/// </summary>
internal sealed class CryptoTestClock : TimeProvider
{
    internal CryptoTestClock(DateTimeOffset now) => Now = now;

    /// <summary>The instant every call to <see cref="GetUtcNow"/> returns.</summary>
    internal DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}
