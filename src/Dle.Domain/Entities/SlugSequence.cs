namespace Dle.Domain.Entities;

/// <summary>
/// A reserved block of slug sequence values. Maps to <c>slug_sequences</c> (ADR-007).
/// </summary>
/// <remarks>
/// <para>
/// Slugs come from a 47 bit counter pushed through a keyed Feistel permutation, which makes the
/// mapping a bijection: every counter value produces exactly one slug, so a collision is
/// structurally impossible and creating a link never needs a retry loop. What remains is handing
/// out counter values, and doing that one row at a time would put a database round trip on every
/// link creation.
/// </para>
/// <para>
/// This entity is the hi/lo block allocator that avoids it: a process claims a range once and then
/// mints slugs from memory. Claimed but unused values are simply skipped, which is harmless because
/// the space holds 1.4 × 10^14 values. The counter must never be reused, so a block is never
/// returned once claimed.
/// </para>
/// </remarks>
public class SlugSequence
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Sequence name, so that a deployment can run more than one independent space.</summary>
    public string Name { get; set; } = "default";

    /// <summary>First value of the reserved block, inclusive.</summary>
    public long BlockStart { get; set; }

    /// <summary>Last value of the reserved block, inclusive.</summary>
    public long BlockEnd { get; set; }

    /// <summary>Identifier of the process that claimed the block, for diagnostics.</summary>
    public string? ClaimedBy { get; set; }

    /// <summary>When the block was claimed.</summary>
    public DateTimeOffset ClaimedAt { get; set; }

    /// <summary>Highest bit position the counter may occupy, so slugs stay eight base62 characters.</summary>
    public const int MaxBits = 47;
}
