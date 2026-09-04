namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="LinkVersion"/> to the <c>link_versions</c> table (FR-107).</summary>
/// <remarks>
/// The table has no tenant column and is listed as tenant exempt in <see cref="DleDbContext"/>: a
/// revision belongs to a link, and every query reaches it through the tenant filtered link.
/// </remarks>
public sealed class LinkVersionConfiguration : IEntityTypeConfiguration<LinkVersion>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LinkVersion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("link_versions");
        builder.HasKey(v => v.Id).HasName("pk_link_versions");

        builder.Property(v => v.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(v => v.LinkId)
            .HasColumnName("link_id")
            .IsRequired();

        builder.Property(v => v.Version)
            .HasColumnName("version")
            .IsRequired();

        builder.Property(v => v.Snapshot)
            .HasColumnName("snapshot")
            .HasColumnType(PostgresConventions.Jsonb)
            .HasDefaultValueSql(PostgresConventions.EmptyJsonObjectDefault)
            .IsRequired();

        builder.Property(v => v.ChangedBy)
            .HasColumnName("changed_by");

        builder.Property(v => v.ChangedAt)
            .HasColumnName("changed_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.Property(v => v.ChangeNote)
            .HasColumnName("change_note");

        builder.HasOne<Link>()
            .WithMany()
            .HasForeignKey(v => v.LinkId)
            .HasConstraintName("fk_link_versions_link")
            .OnDelete(DeleteBehavior.Cascade);

        // One row per revision, and the history is read newest first.
        builder.HasIndex(v => new { v.LinkId, v.Version })
            .IsUnique()
            .HasDatabaseName("uq_link_versions_link_version");
    }
}
