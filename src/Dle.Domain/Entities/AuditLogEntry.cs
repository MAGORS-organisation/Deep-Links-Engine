namespace Dle.Domain.Entities;

/// <summary>
/// One immutable record of an administrative operation (FR-246). Maps to <c>audit_log</c>.
/// </summary>
/// <remarks>
/// <para>
/// An immutable audit log and the right to erasure under article 17 of the GDPR contradict each
/// other the moment the log contains data about a data subject: the log may not be changed, and
/// the data must be deleted. The resolution is structural rather than procedural, and it is
/// visible in the shape of this class — there is deliberately nowhere to put an end user
/// identifier. The entry records only who acted, what they did, which object they did it to and
/// when: operator, action, subject, time (§E.6.3).
/// </para>
/// <para>
/// <see cref="ActorId"/> and <see cref="SubjectId"/> therefore always refer to operators and to
/// configuration objects — a tenant, a link, a domain, an API key — never to a visitor or an
/// installation. <see cref="Metadata"/> is for the details of the change, such as which fields
/// changed and their previous values; putting a click identifier, an install identifier, an IP
/// hash or anything else derived from an end user in it would reintroduce the very conflict this
/// design removes. An erasure request then only ever touches the click stream and the
/// attributions, where no retention obligation stands in its way. This is enforced by a test, not
/// by discipline.
/// </para>
/// </remarks>
public class AuditLogEntry
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>Tenant the operation concerned.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Operator who acted. Null for an action taken by the system itself.</summary>
    public Guid? ActorId { get; set; }

    /// <summary>Kind of actor, stored as text: <c>user</c>, <c>api_key</c> or <c>system</c>.</summary>
    public string ActorType { get; set; } = string.Empty;

    /// <summary>What was done, for example <c>link.created</c> or <c>domain.verified</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Kind of object acted upon, for example <c>link</c>, <c>domain</c> or <c>api_key</c>.</summary>
    public string SubjectType { get; set; } = string.Empty;

    /// <summary>Identifier of that object, as text because subjects use different key types.</summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>Details of the change, stored as a JSON document. Configuration only; never
    /// anything that identifies an end user.</summary>
    public string Metadata { get; set; } = "{}";

    /// <summary>Instant of the operation, in UTC.</summary>
    public DateTimeOffset OccurredAt { get; set; }
}
