using Dle.Domain.Primitives;

namespace Dle.Domain.Routing;

/// <summary>
/// Version constraint on an OS or application version (FR-124).
/// All bounds that are present must hold at the same time.
/// </summary>
public sealed record VersionPredicate
{
    /// <summary>Exact version. <c>"18"</c> equals <c>"18.0"</c> and <c>"18.0.0"</c>.</summary>
    public string? Eq { get; init; }

    /// <summary>Strictly greater than this version.</summary>
    public string? Gt { get; init; }

    /// <summary>Greater than or equal to this version.</summary>
    public string? Gte { get; init; }

    /// <summary>Strictly lower than this version.</summary>
    public string? Lt { get; init; }

    /// <summary>Lower than or equal to this version.</summary>
    public string? Lte { get; init; }

    /// <summary>True when at least one bound is set.</summary>
    private bool HasBound =>
        Eq is not null || Gt is not null || Gte is not null || Lt is not null || Lte is not null;

    /// <summary>
    /// Evaluates the predicate against an observed version.
    /// </summary>
    /// <param name="actual">
    /// The version reported by the client, for example <c>"18.1.2"</c>. Missing components count as zero
    /// and a pre-release suffix is ignored (see <see cref="VersionComparer"/>).
    /// </param>
    /// <returns>
    /// <see langword="true"/> when every bound that is set holds. A predicate with no bounds at all matches
    /// anything, including a missing <paramref name="actual"/>; any predicate with at least one bound cannot
    /// be satisfied by a client whose version is unknown, so that case returns <see langword="false"/>.
    /// </returns>
    public bool Matches(string? actual)
    {
        if (!HasBound)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        if (Eq is not null && VersionComparer.Compare(actual, Eq) != 0)
        {
            return false;
        }

        if (Gt is not null && VersionComparer.Compare(actual, Gt) <= 0)
        {
            return false;
        }

        if (Gte is not null && VersionComparer.Compare(actual, Gte) < 0)
        {
            return false;
        }

        if (Lt is not null && VersionComparer.Compare(actual, Lt) >= 0)
        {
            return false;
        }

        if (Lte is not null && VersionComparer.Compare(actual, Lte) > 0)
        {
            return false;
        }

        return true;
    }
}
