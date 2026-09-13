namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="SdkKey"/> to the <c>sdk_keys</c> table (§E.7, K6).</summary>
public sealed class SdkKeyConfiguration : IEntityTypeConfiguration<SdkKey>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SdkKey> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sdk_keys");
        builder.HasKey(k => k.Id).HasName("pk_sdk_keys");

        builder.Property(k => k.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(k => k.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(k => k.AppId)
            .HasColumnName("app_id")
            .IsRequired();

        builder.Property(k => k.KeyPrefix)
            .HasColumnName("key_prefix")
            .IsRequired();

        builder.Property(k => k.Hash)
            .HasColumnName("hash")
            .IsRequired();

        builder.Property(k => k.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .HasSentinel(true)
            .IsRequired();

        builder.Property(k => k.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.HasOne<App>()
            .WithMany()
            .HasForeignKey(k => k.AppId)
            .HasConstraintName("fk_sdk_keys_app")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(k => k.KeyPrefix).IsUnique().HasDatabaseName("uq_sdk_keys_prefix");
        builder.HasIndex(k => k.AppId).HasDatabaseName("ix_sdk_keys_app");
    }
}
