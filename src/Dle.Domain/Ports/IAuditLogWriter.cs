namespace Dle.Domain.Ports;

/// <summary>
/// Appends immutable entries to the administrative audit log (FR-246, §E.6.3).
/// </summary>
public interface IAuditLogWriter
{
    /// <summary>
    /// Records an administrative operation.
    /// </summary>
    /// <param name="action">What was done, for example <c>link.created</c>.</param>
    /// <param name="subjectType">Kind of object acted upon, for example <c>link</c>.</param>
    /// <param name="subjectId">Identifier of that object or tenant scope, as text.</param>
    /// <param name="actorType">Kind of actor: <c>user</c>, <c>api_key</c> or <c>system</c>.</param>
    /// <param name="actorId">The operator, or <see langword="null"/> for the system itself.</param>
    /// <param name="metadataJson">Details of the change as a JSON object, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The written entry.</returns>
    Task<AuditLogEntry> WriteAsync(
        string action,
        string subjectType,
        string subjectId,
        string actorType,
        Guid? actorId,
        string? metadataJson,
        CancellationToken cancellationToken);
}
