namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="SigningKeyRecord"/> to the <c>signing_keys</c> table (§E.4.2, ADR-013).</summary>
public sealed class SigningKeyRecordConfiguration : IEntityTypeConfiguration<SigningKeyRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SigningKeyRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("signing_keys");
        builder.HasKey(k => k.Id).HasName("pk_signing_keys");

        builder.Property(k => k.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        // Null means an instance wide key. The tenant query filter admits those alongside the
        // tenant's own, because every tenant has to be able to verify a signature made with them.
        builder.Property(k => k.TenantId)
            .HasColumnName("tenant_id");

        builder.Property(k => k.Kid)
            .HasColumnName("kid")
            .IsRequired();

        builder.Property(k => k.Algorithm)
            .HasColumnName("algorithm")
            .IsRequired();

        builder.Property(k => k.PublicKey)
            .HasColumnName("public_key")
            .IsRequired();

        // Encrypted at rest, and never read by the edge: the resolver verifies but does not sign
        // (T-15).
        builder.Property(k => k.PrivateKeyEncrypted)
            .HasColumnName("private_key_encrypted");

        // webhook|click_id|slug|token, as text.
        builder.Property(k => k.Purpose)
            .HasColumnName("purpose")
            .IsRequired();

        builder.Property(k => k.NotBefore)
            .HasColumnName("not_before")
            .IsRequired();

        builder.Property(k => k.NotAfter)
            .HasColumnName("not_after");

        builder.Property(k => k.IsCurrent)
            .HasColumnName("is_current")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(k => k.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        // A signature carries the kid and nothing else, so the identifier has to be unique across
        // the instance for the verifier to resolve it without knowing the tenant first.
        builder.HasIndex(k => k.Kid).IsUnique().HasDatabaseName("uq_signing_keys_kid");

        // Exactly one current key per tenant and purpose. Enforced here rather than in the
        // rotation code, because two current keys would make the signer's choice arbitrary (S-12).
        // NULLS NOT DISTINCT (PostgreSQL 15+, and the project floor is 16) so that the instance
        // wide keys, whose tenant_id is null, are covered by the same constraint instead of
        // slipping past it the way distinct nulls would.
        builder.HasIndex(k => new { k.TenantId, k.Purpose })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("uq_signing_keys_current")
            .HasFilter("is_current");

        builder.HasIndex(k => new { k.Purpose, k.NotAfter })
            .HasDatabaseName("ix_signing_keys_purpose_validity");
    }
}
