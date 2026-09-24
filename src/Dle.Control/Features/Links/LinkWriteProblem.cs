using Dle.Control.Infrastructure;

namespace Dle.Control.Features.Links;

/// <summary>
/// Renders a <see cref="LinkWriteOutcome"/> as an RFC 9457 problem document, or as one line of a
/// bulk result.
/// </summary>
/// <remarks>
/// The two renderings share one source so that a rule cannot be reported differently depending on
/// which endpoint hit it. An integrator who learns that a missing default rule is
/// <see cref="ProblemCodes.MissingDefaultRule"/> on the single-link route finds the same identifier
/// in the <c>error</c> member of a failed bulk line.
/// </remarks>
internal static class LinkWriteProblem
{
    /// <summary>Maps a failure onto its stable problem type.</summary>
    /// <param name="error">The failure.</param>
    /// <returns>One of the identifiers on <see cref="ProblemCodes"/>.</returns>
    internal static string TypeOf(LinkWriteError error) => error switch
    {
        LinkWriteError.SlugInvalid => ProblemCodes.SlugInvalid,
        LinkWriteError.SlugTaken => ProblemCodes.SlugTaken,
        LinkWriteError.UnsafeTarget => ProblemCodes.UnsafeTarget,
        LinkWriteError.MissingDefaultRule => ProblemCodes.MissingDefaultRule,
        LinkWriteError.InvalidRoutingRules => ProblemCodes.InvalidRoutingRules,
        LinkWriteError.DomainNotFound => ProblemCodes.Base + "not-found",
        LinkWriteError.LinkNotFound => ProblemCodes.Base + "not-found",
        LinkWriteError.Conflict => ProblemCodes.WriteConflict,
        LinkWriteError.TooLarge => ProblemCodes.LinkTooLarge,
        LinkWriteError.Validation => ProblemCodes.ValidationFailed,
        LinkWriteError.None => ProblemCodes.ValidationFailed,
        _ => ProblemCodes.ValidationFailed,
    };

    /// <summary>Renders a failure as a problem document.</summary>
    /// <param name="outcome">The failed outcome.</param>
    /// <returns>The result to return from the endpoint.</returns>
    internal static IResult ToResult(LinkWriteOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        string detail = outcome.Detail ?? "The link could not be written.";

        return outcome.Error switch
        {
            // A resource of another tenant is filtered out of the queryable before any handler sees
            // it, so it arrives here as an ordinary miss and leaves as 404 — never 403 (TC-166).
            LinkWriteError.DomainNotFound or LinkWriteError.LinkNotFound => DleProblem.NotFound(detail),

            LinkWriteError.SlugTaken => DleProblem.Conflict(
                ProblemCodes.SlugTaken,
                "The slug is already in use.",
                detail),
            LinkWriteError.Conflict => DleProblem.Conflict(
                ProblemCodes.WriteConflict,
                "The link changed since it was read.",
                detail),
            LinkWriteError.TooLarge => DleProblem.Create(
                StatusCodes.Status422UnprocessableEntity,
                ProblemCodes.LinkTooLarge,
                "The link is too large for the resolve index.",
                detail),

            LinkWriteError.UnsafeTarget => DleProblem.Create(
                StatusCodes.Status422UnprocessableEntity,
                ProblemCodes.UnsafeTarget,
                "The target URL was refused.",
                detail,
                Extensions(outcome)),

            LinkWriteError.MissingDefaultRule => DleProblem.Create(
                StatusCodes.Status400BadRequest,
                ProblemCodes.MissingDefaultRule,
                "The routing rule set has no default rule.",
                detail,
                Extensions(outcome)),

            LinkWriteError.InvalidRoutingRules => DleProblem.Create(
                StatusCodes.Status400BadRequest,
                ProblemCodes.InvalidRoutingRules,
                "The routing rules are invalid.",
                detail,
                Extensions(outcome)),

            LinkWriteError.SlugInvalid => DleProblem.Create(
                StatusCodes.Status400BadRequest,
                ProblemCodes.SlugInvalid,
                "The slug cannot be used.",
                detail,
                Extensions(outcome)),

            LinkWriteError.Validation or LinkWriteError.None => outcome.FieldErrors is null
                ? DleProblem.Create(
                    StatusCodes.Status400BadRequest,
                    ProblemCodes.ValidationFailed,
                    "The request failed validation.",
                    detail)
                : DleProblem.Validation(outcome.FieldErrors, detail),

            _ => DleProblem.Create(
                StatusCodes.Status400BadRequest,
                ProblemCodes.ValidationFailed,
                "The request failed validation.",
                detail),
        };
    }

    /// <summary>Renders a failure as one line of a bulk result (FR-103).</summary>
    /// <param name="reference">The caller's correlation key for the line.</param>
    /// <param name="outcome">The failed outcome.</param>
    /// <returns>The line to write back.</returns>
    internal static BulkLinkResult ToBulkLine(string? reference, LinkWriteOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return new BulkLinkResult
        {
            Ref = reference,
            Ok = false,
            Error = TypeOf(outcome.Error),
            Detail = outcome.Detail,
        };
    }

    /// <summary>Wraps the field errors as the <c>errors</c> extension member.</summary>
    /// <param name="outcome">The failed outcome.</param>
    /// <returns>The extension members, or <see langword="null"/>.</returns>
    private static Dictionary<string, object?>? Extensions(LinkWriteOutcome outcome) =>
        outcome.FieldErrors is null
            ? null
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [DleProblem.ErrorsExtension] = outcome.FieldErrors,
            };
}
