namespace Dle.Persistence.Tenancy;

/// <summary>
/// Thrown when the control plane touches tenant-owned data without a tenant in scope.
/// </summary>
/// <remarks>
/// This is the failure mode that replaces the silent one. Without it a query that forgets the
/// tenant would simply return the rows of every tenant, which is the leak T-09 describes; with it
/// the query cannot execute at all. If the operation genuinely spans tenants, open a
/// <see cref="DleDbContext.BeginCrossTenantScope"/> and say why.
/// </remarks>
public sealed class TenantContextMissingException : InvalidOperationException
{
    private const string DefaultMessage =
        "No tenant is in scope. Establish one with ITenantContext.BeginScope before touching " +
        "tenant owned data, or open DleDbContext.BeginCrossTenantScope for deliberate cross " +
        "tenant work.";

    /// <summary>Creates the exception with the standard explanation.</summary>
    public TenantContextMissingException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Creates the exception with a caller supplied message.</summary>
    /// <param name="message">The message.</param>
    public TenantContextMissingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public TenantContextMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
