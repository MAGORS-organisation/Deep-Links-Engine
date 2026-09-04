using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;

using Dle.Control.Features.Shared;

using Microsoft.AspNetCore.Http;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// <c>POST</c> and <c>DELETE /api/v1/abuse/links/{linkId}/quarantine</c> — enforcement (TC-103).
/// </summary>
/// <remarks>
/// An instance operator action, not a tenant one: the party whose link is being withdrawn is not
/// the party who decides. The link is addressed by identifier across tenants, which is why these
/// endpoints live in the abuse feature rather than alongside the tenant's own link management.
/// </remarks>
public static class QuarantineLink
{
    /// <summary>Withdraws a link from service.</summary>
    /// <param name="linkId">The link identifier, as text because identifiers are 64 bit.</param>
    /// <param name="request">The stated reason.</param>
    /// <param name="user">The operator taking the decision.</param>
    /// <param name="locator">Locates the link across tenants.</param>
    /// <param name="quarantine">Enforcement service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 when withdrawn or already withdrawn, 404 when there is no such link.</returns>
    public static async Task<IResult> QuarantineAsync(
        string linkId,
        QuarantineDecisionRequest request,
        ClaimsPrincipal user,
        AbuseLinkLocator locator,
        LinkQuarantineService quarantine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(quarantine);

        if (!TryParseLinkId(linkId, out long id))
        {
            return DleProblemResults.NotFound("There is no link with that identifier.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return DleProblemResults.ValidationFailed(
                "A quarantine decision has to carry a reason.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["reason"] = ["Say why the link is being withdrawn. It is served with the 410 trail."],
                });
        }

        LocatedLink? link = await locator.FindByIdAsync(id, cancellationToken);

        if (link is null)
        {
            return DleProblemResults.NotFound("There is no link with that identifier.");
        }

        await quarantine.QuarantineAsync(
            link,
            request.Reason,
            RequestActor.FromPrincipal(user),
            cancellationToken);

        // 204 whether or not this call was the one that changed the state: the caller asked for the
        // link to be out of service, and it is.
        return TypedResults.NoContent();
    }

    /// <summary>Returns a withdrawn link to service after a successful appeal.</summary>
    /// <param name="linkId">The link identifier.</param>
    /// <param name="request">The stated reason.</param>
    /// <param name="user">The operator taking the decision.</param>
    /// <param name="locator">Locates the link across tenants.</param>
    /// <param name="quarantine">Enforcement service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 when released or already in service, 404 when there is no such link.</returns>
    public static async Task<IResult> ReleaseAsync(
        string linkId,
        QuarantineDecisionRequest request,
        ClaimsPrincipal user,
        AbuseLinkLocator locator,
        LinkQuarantineService quarantine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(quarantine);

        if (!TryParseLinkId(linkId, out long id))
        {
            return DleProblemResults.NotFound("There is no link with that identifier.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return DleProblemResults.ValidationFailed(
                "Lifting a quarantine has to carry a reason.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["reason"] = ["Say why the withdrawal is being lifted. The appeal is part of the record."],
                });
        }

        LocatedLink? link = await locator.FindByIdAsync(id, cancellationToken);

        if (link is null)
        {
            return DleProblemResults.NotFound("There is no link with that identifier.");
        }

        await quarantine.ReleaseAsync(
            link,
            request.Reason,
            RequestActor.FromPrincipal(user),
            cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>Parses the route value.</summary>
    /// <param name="linkId">The route value.</param>
    /// <param name="id">The parsed identifier.</param>
    /// <returns><see langword="true"/> when the value is a link identifier.</returns>
    private static bool TryParseLinkId(string? linkId, out long id) =>
        long.TryParse(linkId, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
}

/// <summary>Body of a quarantine or release decision.</summary>
public sealed record QuarantineDecisionRequest
{
    /// <summary>
    /// Why the link is being withdrawn, or why the withdrawal is being lifted. Recorded in the
    /// audit trail and sent to the owning tenant.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(1000, MinimumLength = 1)]
    public required string Reason { get; init; }
}
