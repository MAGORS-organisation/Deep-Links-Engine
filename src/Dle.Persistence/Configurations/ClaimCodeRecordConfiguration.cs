namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="ClaimCodeRecord"/> to the <c>claim_codes</c> table (FR-184).</summary>
public sealed class ClaimCodeRecordConfiguration : IEntityTypeConfiguration<ClaimCodeRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ClaimCodeRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("claim_codes");
        builder.HasKey(c => c.Id).HasName("pk_claim_codes");

        builder.Property(c => c.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(c => c.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        // Only the hash: the code is six characters from a 26 symbol alphabet, so a leaked table
        // must not hand an attacker a set of valid claims (§E.4.1, K3).
        builder.Property(c => c.CodeHash)
            .HasColumnName("code_hash")
            .IsRequired();

        builder.Property(c => c.ClickId)
            .HasColumnName("click_id");

        builder.Property(c => c.LinkId)
            .HasColumnName("link_id");

        builder.Property(c => c.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();

        builder.Property(c => c.ConsumedAt)
            .HasColumnName("consumed_at");

        builder.Property(c => c.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        // Redemption looks the code up by hash within the tenant, so the pair is the lookup key.
        builder.HasIndex(c => new { c.TenantId, c.CodeHash })
            .IsUnique()
            .HasDatabaseName("uq_claim_codes_tenant_hash");

        // The pruning job deletes everything already expired.
        builder.HasIndex(c => c.ExpiresAt).HasDatabaseName("ix_claim_codes_expires");
    }
}
