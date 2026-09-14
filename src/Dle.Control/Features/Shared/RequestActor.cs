using System.Security.Claims;

namespace Dle.Control.Features.Shared;

/// <summary>
/// Who is making the request, in the terms the audit trail records (FR-246, §E.6.3).
/// </summary>
/// <param name="ActorType">Kind of actor: <c>user</c>, <c>api_key</c> or <c>system</c>.</param>
/// <param name="ActorId">Identifier of the operator, or <see langword="null"/> for the system.</param>
/// <remarks>
/// The audit trail records an operator, an action, an object and a time, and deliberately has
/// nowhere to put an end user identifier. That is what keeps an append-only trail compatible with
/// the right to erasure: erasure only ever touches the click stream and the attributions.
/// </remarks>
internal readonly record struct RequestActor(string ActorType, Guid? ActorId)
{
    /// <summary>An action taken by the engine itself, for example by a worker.</summary>
    internal static RequestActor System { get; } = new("system", null);

    /// <summary>
    /// Reads the actor out of the authenticated principal.
    /// </summary>
    /// <param name="principal">The principal, or <see langword="null"/>.</param>
    /// <returns>
    /// The actor. An unauthenticated principal is reported as the system rather than as an unknown
    /// user, so an entry never claims an operator the engine cannot name.
    /// </returns>
    /// <remarks>
    /// Both claim names are read because the two credentials the control plane accepts spell the
    /// subject differently: an OIDC token uses <c>sub</c> and the API key handler is free to use the
    /// framework's own <see cref="ClaimTypes.NameIdentifier"/>. A subject that is not a GUID — a
    /// federated identity, an email address — is recorded as an actor without an identifier rather
    /// than dropped, because the fact that a human did this is itself part of the record.
    /// </remarks>
    internal static RequestActor FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is null || !principal.Identity.IsAuthenticated)
        {
            return System;
        }

        string actorType = principal.HasClaim(
            static claim => string.Equals(claim.Type, "dle:actor_type", StringComparison.Ordinal))
            ? principal.FindFirstValue("dle:actor_type") ?? "user"
            : "user";

        string? subject = principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(subject, out Guid actorId)
            ? new RequestActor(actorType, actorId)
            : new RequestActor(actorType, null);
    }
}
