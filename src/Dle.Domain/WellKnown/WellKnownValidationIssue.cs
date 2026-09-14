namespace Dle.Domain.WellKnown;

/// <summary>
/// A single finding produced by <see cref="WellKnownValidator"/> when a live domain's well-known file
/// is checked (FR-143, FR-144).
/// </summary>
/// <param name="Code">A stable machine-readable code; one of the <c>Err*</c>/<c>Warn*</c> constants on <see cref="WellKnownValidator"/>.</param>
/// <param name="Message">Human readable explanation, in English, shown in the domain verification UI.</param>
/// <param name="IsError">
/// <see langword="true"/> when the finding means deep linking is broken on the platform, and
/// <see langword="false"/> when it is a warning the operator should look at but which does not by
/// itself break anything.
/// </param>
public sealed record WellKnownValidationIssue(string Code, string Message, bool IsError);
