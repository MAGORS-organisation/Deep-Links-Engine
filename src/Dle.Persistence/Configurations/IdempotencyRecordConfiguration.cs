namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="IdempotencyRecord"/> to the <c>idempotency_records</c> table (§B.7.3).</summary>
public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("idempotency_records");

        // The key is scoped to the tenant and to the endpoint, so the same key on a different route
        // conflicts rather than replaying the wrong response. Tenant first, because that is also
        // the order the tenant query filter probes in.
        builder.HasKey(r => new { r.TenantId, r.Endpoint, r.Key }).HasName("pk_idempotency_records");

        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.Endpoint).HasColumnName("endpoint");
        builder.Property(r => r.Key).HasColumnName("key");

        // Compared against the hash of the replayed body: the same key with a different body is a
        // client bug, not a retry, and answering it with the first response would hide the mistake.
        builder.Property(r => r.RequestHash)
            .HasColumnName("request_hash")
            .IsRequired();

        builder.Property(r => r.ResponseStatus)
            .HasColumnName("response_status")
            .IsRequired();

        builder.Property(r => r.ResponseBody)
            .HasColumnName("response_body");

        builder.Property(r => r.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.Property(r => r.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();

        // The pruning job deletes everything already expired.
        builder.HasIndex(r => r.ExpiresAt).HasDatabaseName("ix_idempotency_records_expires");
    }
}
