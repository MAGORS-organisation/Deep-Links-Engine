namespace Dle.SecurityTests.Infrastructure;

/// <summary>
/// A clock the test moves by hand.
/// </summary>
/// <remarks>
/// The shared kernel forbids <c>DateTime.UtcNow</c> in product code precisely so that time can be an
/// input here. A security test that depends on the wall clock is worse than useless: a signature
/// tolerance test that passes at 10:00 and fails at 10:00:01 gets marked flaky and then muted, and the
/// replay window it guarded stops being guarded.
/// </remarks>
public sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    /// <summary>Creates the clock at an instant.</summary>
    /// <param name="now">The instant.</param>
    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    /// <summary>Creates the clock at a fixed, arbitrary instant in 2026.</summary>
    public FixedTimeProvider()
        : this(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero))
    {
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="delta">How far.</param>
    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
