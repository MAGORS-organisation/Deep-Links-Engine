namespace Dle.UnitTests.Edge;

/// <summary>
/// A clock the test drives. SHARED-KERNEL §17.2 forbids reading the machine clock in engine code;
/// a test that depends on wall-clock time is a defect for the same reason.
/// </summary>
internal sealed class EdgeTestClock : TimeProvider
{
    internal EdgeTestClock(DateTimeOffset now) => Now = now;

    /// <summary>The instant every call to <see cref="GetUtcNow"/> returns.</summary>
    internal DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}
