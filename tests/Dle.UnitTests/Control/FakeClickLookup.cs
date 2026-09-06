using Dle.Domain.Ports;

namespace Dle.UnitTests.Control;

/// <summary>
/// A click stream that answers from a list, and remembers what it was asked. The record of what was
/// asked is the assertion for TC-145 and TC-146: when the probabilistic module is off or consent is
/// absent, no candidate query may happen at all.
/// </summary>
internal sealed class FakeClickLookup : IClickLookup
{
    private readonly List<ClickRecord> _clicks = [];

    /// <summary>Number of times a candidate set was asked for.</summary>
    internal int CandidateQueries { get; private set; }

    /// <summary>Number of times a click was looked up by identifier.</summary>
    internal int ClickIdLookups { get; private set; }

    /// <summary>The time window of the last candidate query.</summary>
    internal (DateTimeOffset From, DateTimeOffset To)? LastCandidateWindow { get; private set; }

    /// <summary>The time window of the last lookup by identifier.</summary>
    internal (DateTimeOffset From, DateTimeOffset To)? LastClickIdWindow { get; private set; }

    internal void Add(ClickRecord click)
    {
        ArgumentNullException.ThrowIfNull(click);
        _clicks.Add(click);
    }

    public ValueTask<ClickRecord?> FindByClickIdAsync(
        string clickId,
        DateTimeOffset hintFrom,
        DateTimeOffset hintTo,
        CancellationToken ct)
    {
        ClickIdLookups++;
        LastClickIdWindow = (hintFrom, hintTo);

        ClickRecord? found = _clicks.Find(click =>
            string.Equals(click.ClickId, clickId, StringComparison.Ordinal) &&
            click.OccurredAt >= hintFrom &&
            click.OccurredAt <= hintTo);

        return ValueTask.FromResult(found);
    }

    public ValueTask<IReadOnlyList<ClickRecord>> FindCandidatesAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? ipPrefix,
        string? osFamily,
        CancellationToken ct)
    {
        CandidateQueries++;
        LastCandidateWindow = (from, to);

        IReadOnlyList<ClickRecord> candidates = _clicks.FindAll(click => click.TenantId == tenantId);

        return ValueTask.FromResult(candidates);
    }
}
