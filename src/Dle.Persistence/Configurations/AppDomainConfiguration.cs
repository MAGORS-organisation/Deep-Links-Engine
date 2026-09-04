namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="AppDomainEntity"/> to the <c>app_domains</c> join table (§B.5.2).</summary>
/// <remarks>
/// The table carries no tenant column, exactly as the DDL specifies, and is therefore listed as
/// tenant exempt in <see cref="DleDbContext"/>. Isolation is not lost: both foreign keys point at
/// tenant filtered tables, so a row can only ever be reached through a pairing the tenant already
/// owns, and <c>AppRepository</c> only ever reaches it through them.
/// </remarks>
public sealed class AppDomainConfiguration : IEntityTypeConfiguration<AppDomainEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AppDomainEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("app_domains");
        builder.HasKey(ad => new { ad.AppId, ad.DomainId }).HasName("pk_app_domains");

        builder.Property(ad => ad.AppId).HasColumnName("app_id");
        builder.Property(ad => ad.DomainId).HasColumnName("domain_id");

        builder.HasOne<App>()
            .WithMany()
            .HasForeignKey(ad => ad.AppId)
            .HasConstraintName("fk_app_domains_app")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<LinkDomain>()
            .WithMany()
            .HasForeignKey(ad => ad.DomainId)
            .HasConstraintName("fk_app_domains_domain")
            .OnDelete(DeleteBehavior.Cascade);

        // The association files are built per host, so the lookup direction that matters is
        // "which applications does this domain serve" (FR-141, FR-142).
        builder.HasIndex(ad => ad.DomainId).HasDatabaseName("ix_app_domains_domain");
    }
}
