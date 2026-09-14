namespace Dle.Persistence.Tenancy;

/// <summary>
/// Thrown when a save would write a row belonging to a tenant other than the one in scope.
/// </summary>
/// <remarks>
/// Query filters keep foreign rows out of reads, but nothing in a query filter stops code from
/// attaching an entity carrying someone else's tenant identifier and saving it. This exception is
/// the write-side half of the same guarantee, raised by <see cref="DbContext.SaveChanges()"/>
/// before the statement reaches the database (T-09).
/// </remarks>
public sealed class CrossTenantWriteException : InvalidOperationException
{
    /// <summary>Creates the exception for a concrete mismatch.</summary>
    /// <param name="entityType">Name of the entity type being written.</param>
    /// <param name="expected">The tenant in scope.</param>
    /// <param name="actual">The tenant carried by the entity.</param>
    public CrossTenantWriteException(string entityType, Guid expected, Guid actual)
        : base(string.Create(
            CultureInfo.InvariantCulture,
            $"A {entityType} belonging to tenant {actual} cannot be written while tenant {expected} is in scope."))
    {
        EntityType = entityType;
        ExpectedTenantId = expected;
        ActualTenantId = actual;
    }

    /// <summary>Creates the exception with a caller supplied message.</summary>
    /// <param name="message">The message.</param>
    public CrossTenantWriteException(string message)
        : base(message)
    {
        EntityType = string.Empty;
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public CrossTenantWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
        EntityType = string.Empty;
    }

    /// <summary>Creates the exception with no detail.</summary>
    public CrossTenantWriteException()
        : base("A row belonging to another tenant cannot be written.")
    {
        EntityType = string.Empty;
    }

    /// <summary>Name of the entity type that failed the check.</summary>
    public string EntityType { get; }

    /// <summary>The tenant that was in scope.</summary>
    public Guid ExpectedTenantId { get; }

    /// <summary>The tenant the entity carried.</summary>
    public Guid ActualTenantId { get; }
}
