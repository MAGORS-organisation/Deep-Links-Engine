namespace Dle.UnitTests.Edge;

/// <summary>
/// A clock the test drives. Engine code may not read the machine clock;
/// a test that depends on wall-clock time is a defect for the same reason.
/// </summary>
internal sealed class EdgeTestClock : TimeProvider
{
    internal EdgeTestClock(DateTimeOffset now) => Now = now;

    /// <summary>The instant every call to <see cref="GetUtcNow"/> returns.</summary>
    internal DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}
