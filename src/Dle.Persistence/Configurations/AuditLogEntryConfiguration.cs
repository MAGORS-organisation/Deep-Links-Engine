namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="AuditLogEntry"/> to the <c>audit_log</c> table (FR-246, §E.6.3).</summary>
/// <remarks>
/// There is deliberately no column here that could hold an end user identifier. An immutable audit
/// log and the right to erasure only conflict once the log contains data about a data subject; the
/// resolution is structural, and this table is where the structure lives.
/// </remarks>
public sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_log");
        builder.HasKey(e => e.Id).HasName("pk_audit_log");

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(e => e.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(e => e.ActorId)
            .HasColumnName("actor_id");

        // user|api_key|system, as text.
        builder.Property(e => e.ActorType)
            .HasColumnName("actor_type")
            .IsRequired();

        builder.Property(e => e.Action)
            .HasColumnName("action")
            .IsRequired();

        builder.Property(e => e.SubjectType)
            .HasColumnName("subject_type")
            .IsRequired();

        // Text, because subjects use different key types: a link is a bigint, a domain a uuid.
        builder.Property(e => e.SubjectId)
            .HasColumnName("subject_id")
            .IsRequired();

        builder.Property(e => e.Metadata)
            .HasColumnName("metadata")
            .HasColumnType(PostgresConventions.Jsonb)
            .HasDefaultValueSql(PostgresConventions.EmptyJsonObjectDefault)
            .IsRequired();

        builder.Property(e => e.OccurredAt)
            .HasColumnName("occurred_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_audit_log_tenant_occurred");

        // "Everything that happened to this link" is the question asked after an incident.
        builder.HasIndex(e => new { e.TenantId, e.SubjectType, e.SubjectId })
            .HasDatabaseName("ix_audit_log_subject");
    }
}
