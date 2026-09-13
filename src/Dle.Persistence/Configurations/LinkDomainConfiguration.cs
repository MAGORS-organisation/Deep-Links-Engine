namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="LinkDomain"/> to the <c>domains</c> table (§B.5.2).</summary>
public sealed class LinkDomainConfiguration : IEntityTypeConfiguration<LinkDomain>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LinkDomain> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("domains");
        builder.HasKey(d => d.Id).HasName("pk_domains");

        builder.Property(d => d.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(d => d.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        // The host is normalized by HostNormalizer before it ever reaches this column; citext then
        // makes the uniqueness check case insensitive without a functional index.
        builder.Property(d => d.Host)
            .HasColumnName("host")
            .HasColumnType(PostgresConventions.CiText)
            .IsRequired();

        builder.Property(d => d.IsDefault)
            .HasColumnName("is_default")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(d => d.TlsStatus)
            .HasColumnName("tls_status")
            .HasDefaultValue("pending")
            .IsRequired();

        builder.Property(d => d.AasaStatus)
            .HasColumnName("aasa_status")
            .HasDefaultValue("pending")
            .IsRequired();

        builder.Property(d => d.AssetlinksStatus)
            .HasColumnName("assetlinks_status")
            .HasDefaultValue("pending")
            .IsRequired();

        builder.Property(d => d.LastVerifiedAt)
            .HasColumnName("last_verified_at");

        builder.Property(d => d.VerificationLog)
            .HasColumnName("verification_log")
            .HasColumnType(PostgresConventions.Jsonb)
            .HasDefaultValueSql(PostgresConventions.EmptyJsonArrayDefault)
            .IsRequired();

        // Nullable: absent means "inherit the tenant mode". It can only ever narrow it (§E.6.2).
        builder.Property(d => d.ConsentModeOverride)
            .HasColumnName("consent_mode_override");

        builder.Property(d => d.Branding)
            .HasColumnName("branding")
            .HasColumnType(PostgresConventions.Jsonb)
            .HasDefaultValueSql(PostgresConventions.EmptyJsonObjectDefault)
            .IsRequired();

        builder.Property(d => d.DefaultOg)
            .HasColumnName("default_og")
            .HasColumnType(PostgresConventions.Jsonb)
            .HasDefaultValueSql(PostgresConventions.EmptyJsonObjectDefault)
            .IsRequired();

        builder.Property(d => d.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .HasSentinel(true)
            .IsRequired();

        builder.Property(d => d.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        // Globally unique, not unique per tenant: a host resolves to exactly one tenant, which is
        // what lets the edge turn a Host header into a tenant without a second lookup.
        builder.HasIndex(d => d.Host).IsUnique().HasDatabaseName("uq_domains_host");
        builder.HasIndex(d => d.TenantId).HasDatabaseName("ix_domains_tenant");
    }
}
