namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="ApiKey"/> to the <c>api_keys</c> table (FR-242).</summary>
public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("api_keys");
        builder.HasKey(k => k.Id).HasName("pk_api_keys");

        builder.Property(k => k.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(k => k.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(k => k.Name)
            .HasColumnName("name")
            .IsRequired();

        // The public half of the credential. It exists so a key can be located without hashing
        // every row; it is not a secret and carries no entropy claim.
        builder.Property(k => k.Prefix)
            .HasColumnName("prefix")
            .IsRequired();

        // Argon2id output. Compared with CryptographicOperations.FixedTimeEquals, never with
        // equality (shared kernel §17.6).
        builder.Property(k => k.Hash)
            .HasColumnName("hash")
            .IsRequired();

        builder.Property(k => k.Role)
            .HasColumnName("role")
            .IsRequired();

        builder.Property(k => k.Scopes)
            .HasColumnName("scopes")
            .HasColumnType(PostgresConventions.TextArray)
            .HasDefaultValueSql(PostgresConventions.EmptyTextArrayDefault)
            .IsRequired();

        builder.Property(k => k.LastUsedAt)
            .HasColumnName("last_used_at");

        builder.Property(k => k.ExpiresAt)
            .HasColumnName("expires_at");

        builder.Property(k => k.RevokedAt)
            .HasColumnName("revoked_at");

        builder.Property(k => k.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(k => k.TenantId)
            .HasConstraintName("fk_api_keys_tenant")
            .OnDelete(DeleteBehavior.Cascade);

        // Authentication looks the key up by prefix on every request, so the prefix is unique
        // across the instance rather than per tenant: the tenant is a result of the lookup, not an
        // input to it.
        builder.HasIndex(k => k.Prefix).IsUnique().HasDatabaseName("uq_api_keys_prefix");
        builder.HasIndex(k => k.TenantId).HasDatabaseName("ix_api_keys_tenant");
    }
}
