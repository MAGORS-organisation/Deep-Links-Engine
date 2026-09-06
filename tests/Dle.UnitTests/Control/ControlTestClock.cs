namespace Dle.UnitTests.Control;

/// <summary>
/// A clock the test drives. Attribution decisions are all relative to "now" — windows, expiry,
/// elapsed minutes in the evidence — so the instant has to be an input, never a reading of the
/// machine clock (SHARED-KERNEL §17.2).
/// </summary>
internal sealed class ControlTestClock : TimeProvider
{
    internal ControlTestClock(DateTimeOffset now) => Now = now;

    /// <summary>The instant every call to <see cref="GetUtcNow"/> returns.</summary>
    internal DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}
