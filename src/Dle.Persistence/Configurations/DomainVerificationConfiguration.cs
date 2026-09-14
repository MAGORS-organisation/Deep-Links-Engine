namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="DomainVerification"/> to the <c>domain_verifications</c> table (FR-143).</summary>
/// <remarks>
/// The table has no tenant column and is listed as tenant exempt in <see cref="DleDbContext"/>:
/// a check result belongs to a domain, and every query reaches it through the tenant filtered
/// domain.
/// </remarks>
public sealed class DomainVerificationConfiguration : IEntityTypeConfiguration<DomainVerification>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DomainVerification> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("domain_verifications");
        builder.HasKey(v => v.Id).HasName("pk_domain_verifications");

        builder.Property(v => v.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(v => v.DomainId)
            .HasColumnName("domain_id")
            .IsRequired();

        // dns|tls|aasa|assetlinks, as text.
        builder.Property(v => v.Kind)
            .HasColumnName("kind")
            .IsRequired();

        // ok|warning|failed, as text.
        builder.Property(v => v.Status)
            .HasColumnName("status")
            .IsRequired();

        builder.Property(v => v.HttpStatus)
            .HasColumnName("http_status");

        // Any value above zero fails the check: both Apple and Google refuse a redirected
        // association file, and that is one of the most common silent breakages (TC-124).
        builder.Property(v => v.RedirectCount)
            .HasColumnName("redirect_count")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(v => v.Issues)
            .HasColumnName("issues")
            .HasColumnType(PostgresConventions.Jsonb)
            .HasDefaultValueSql(PostgresConventions.EmptyJsonArrayDefault)
            .IsRequired();

        builder.Property(v => v.CheckedAt)
            .HasColumnName("checked_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.HasOne<LinkDomain>()
            .WithMany()
            .HasForeignKey(v => v.DomainId)
            .HasConstraintName("fk_domain_verifications_domain")
            .OnDelete(DeleteBehavior.Cascade);

        // The useful question is "since when has this been failing", so the history is read per
        // domain and kind, newest first.
        builder.HasIndex(v => new { v.DomainId, v.Kind, v.CheckedAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_domain_verifications_domain_kind_checked");
    }
}
