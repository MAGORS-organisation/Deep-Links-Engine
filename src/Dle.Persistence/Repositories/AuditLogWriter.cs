using System.Text.Json;

namespace Dle.Persistence.Repositories;

/// <summary>
/// Appends entries to the administrative audit trail (FR-246, §E.6.3).
/// </summary>
/// <remarks>
/// Append only, by design and not merely by convention: there is no update and no delete on this
/// class. What keeps that compatible with the right to erasure is the shape of the entry — it
/// records an operator, an action, a configuration object and a time, and has nowhere to put an end
/// user identifier. An erasure request therefore only ever touches the click stream and the
/// attributions, where no retention obligation stands in the way.
/// </remarks>
public sealed class AuditLogWriter
{
    /// <summary>
    /// Metadata keys that would reintroduce end user data into the audit trail and are refused.
    /// </summary>
    private static readonly string[] ForbiddenMetadataKeys =
    [
        "click_id",
        "clickId",
        "install_id",
        "installId",
        "ip",
        "ip_hash",
        "ipHash",
        "ip_prefix",
        "ipPrefix",
        "user_agent",
        "userAgent",
        "login_key",
        "loginKey",
    ];

    private readonly DleDbContext _db;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the writer.</summary>
    /// <param name="db">The control-plane context.</param>
    /// <param name="timeProvider">Clock used to stamp entries.</param>
    public AuditLogWriter(DleDbContext db, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _db = db;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Records an administrative operation.
    /// </summary>
    /// <param name="action">What was done, for example <c>link.created</c>.</param>
    /// <param name="subjectType">Kind of object acted upon, for example <c>link</c>.</param>
    /// <param name="subjectId">Identifier of that object, as text.</param>
    /// <param name="actorType">Kind of actor: <c>user</c>, <c>api_key</c> or <c>system</c>.</param>
    /// <param name="actorId">The operator, or <see langword="null"/> for the system itself.</param>
    /// <param name="metadataJson">Details of the change as a JSON object, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The written entry.</returns>
    /// <exception cref="ArgumentException">The metadata is not a JSON object, or carries a key that
    /// would identify an end user.</exception>
    public async Task<AuditLogEntry> WriteAsync(
        string action,
        string subjectType,
        string subjectId,
        string actorType,
        Guid? actorId,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectType);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorType);

        string metadata = metadataJson ?? "{}";
        ValidateMetadata(metadata, nameof(metadataJson));

        AuditLogEntry entry = new()
        {
            ActorId = actorId,
            ActorType = actorType,
            Action = action,
            SubjectType = subjectType,
            SubjectId = subjectId,
            Metadata = metadata,
            OccurredAt = _timeProvider.GetUtcNow(),
        };

        _db.AuditLog.Add(entry);
        await _db.SaveChangesAsync(cancellationToken);
        return entry;
    }

    /// <summary>Reads the audit trail of the tenant in scope, newest first.</summary>
    /// <param name="subjectType">Restrict to one kind of object, or <see langword="null"/>.</param>
    /// <param name="subjectId">Restrict to one object, or <see langword="null"/>.</param>
    /// <param name="limit">Maximum number of rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entries.</returns>
    public async Task<IReadOnlyList<AuditLogEntry>> ReadAsync(
        string? subjectType,
        string? subjectId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        IQueryable<AuditLogEntry> query = _db.AuditLog.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(subjectType))
        {
            query = query.Where(e => e.SubjectType == subjectType);
        }

        if (!string.IsNullOrWhiteSpace(subjectId))
        {
            query = query.Where(e => e.SubjectId == subjectId);
        }

        return await query
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Refuses metadata that is not a JSON object or that carries an end user identifier.
    /// </summary>
    /// <param name="metadataJson">The candidate document.</param>
    /// <param name="parameterName">Name of the offending parameter, for the exception.</param>
    /// <exception cref="ArgumentException">The document is unusable.</exception>
    /// <remarks>
    /// The invariant of §E.6.3 — that the audit trail never contains data about an end user — is
    /// worth nothing if it depends on every caller remembering it. This is the enforcement, checked
    /// on the way in, where the caller still knows what it was trying to write.
    /// </remarks>
    private static void ValidateMetadata(string metadataJson, string parameterName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(metadataJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Audit metadata must be a JSON document.", parameterName, exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "Audit metadata must be a JSON object.", parameterName);
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                foreach (string forbidden in ForbiddenMetadataKeys)
                {
                    if (string.Equals(property.Name, forbidden, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException(
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"Audit metadata must not carry '{property.Name}': the audit trail " +
                                $"is immutable and therefore may never contain data about an end " +
                                $"user (§E.6.3)."),
                            parameterName);
                    }
                }
            }
        }
    }
}
