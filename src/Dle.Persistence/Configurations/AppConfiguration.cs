namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="App"/> to the <c>apps</c> table (§B.5.2).</summary>
public sealed class AppConfiguration : IEntityTypeConfiguration<App>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<App> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("apps");
        builder.HasKey(a => a.Id).HasName("pk_apps");

        builder.Property(a => a.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(a => a.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        // ios|android as text.
        builder.Property(a => a.Platform)
            .HasColumnName("platform")
            .IsRequired();

        builder.Property(a => a.BundleId)
            .HasColumnName("bundle_id")
            .IsRequired();

        builder.Property(a => a.TeamId)
            .HasColumnName("team_id");

        builder.Property(a => a.CertFingerprints)
            .HasColumnName("cert_fingerprints")
            .HasColumnType(PostgresConventions.TextArray)
            .HasDefaultValueSql(PostgresConventions.EmptyTextArrayDefault)
            .IsRequired();

        // Kept apart from cert_fingerprints so the validator can tell an upload certificate from a
        // Play App Signing certificate, which is the most common silent breakage of Android app
        // links (FR-144, TC-123).
        builder.Property(a => a.PlaySigningFingerprints)
            .HasColumnName("play_signing_fingerprints")
            .HasColumnType(PostgresConventions.TextArray)
            .HasDefaultValueSql(PostgresConventions.EmptyTextArrayDefault)
            .IsRequired();

        builder.Property(a => a.StoreId)
            .HasColumnName("store_id");

        builder.Property(a => a.CustomScheme)
            .HasColumnName("custom_scheme");

        builder.Property(a => a.MinAppVersion)
            .HasColumnName("min_app_version");

        builder.Property(a => a.AppClipBundleId)
            .HasColumnName("appclip_bundle_id");

        builder.Property(a => a.StoreUrl)
            .HasColumnName("store_url");

        builder.Property(a => a.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.HasIndex(a => new { a.TenantId, a.Platform, a.BundleId })
            .IsUnique()
            .HasDatabaseName("uq_apps_tenant_platform_bundle");
    }
}
