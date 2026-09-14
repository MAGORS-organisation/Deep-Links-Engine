namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="Install"/> to the <c>installs</c> table (§B.5.3).</summary>
public sealed class InstallConfiguration : IEntityTypeConfiguration<Install>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Install> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("installs");
        builder.HasKey(i => i.Id).HasName("pk_installs");

        builder.Property(i => i.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(i => i.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(i => i.AppId)
            .HasColumnName("app_id")
            .IsRequired();

        builder.Property(i => i.InstallId)
            .HasColumnName("install_id")
            .IsRequired();

        builder.Property(i => i.FirstOpenAt)
            .HasColumnName("first_open_at")
            .IsRequired();

        builder.Property(i => i.RawReferrer)
            .HasColumnName("raw_referrer");

        builder.Property(i => i.Platform)
            .HasColumnName("platform")
            .IsRequired();

        builder.Property(i => i.AppVersion)
            .HasColumnName("app_version");

        builder.Property(i => i.LoginKeyHash)
            .HasColumnName("login_key_hash");

        builder.Property(i => i.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.HasOne<App>()
            .WithMany()
            .HasForeignKey(i => i.AppId)
            .HasConstraintName("fk_installs_app")
            .OnDelete(DeleteBehavior.Cascade);

        // This is what makes a repeated resolve idempotent: the second call finds the existing row
        // and returns the existing attribution instead of creating a second one (TC-143, FR-188).
        builder.HasIndex(i => new { i.AppId, i.InstallId })
            .IsUnique()
            .HasDatabaseName("uq_installs_app_install");

        // Login reconciliation (strategy S2) looks an installation up by the hashed account key.
        builder.HasIndex(i => new { i.TenantId, i.LoginKeyHash })
            .HasDatabaseName("ix_installs_login_key")
            .HasFilter("login_key_hash IS NOT NULL");
    }
}
