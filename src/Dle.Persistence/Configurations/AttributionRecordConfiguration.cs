namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="AttributionRecord"/> to the <c>attributions</c> table (§B.5.3).</summary>
public sealed class AttributionRecordConfiguration : IEntityTypeConfiguration<AttributionRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AttributionRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("attributions");
        builder.HasKey(a => a.Id).HasName("pk_attributions");

        builder.Property(a => a.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(a => a.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(a => a.InstallId)
            .HasColumnName("install_id")
            .IsRequired();

        builder.Property(a => a.ClickId)
            .HasColumnName("click_id");

        builder.Property(a => a.LinkId)
            .HasColumnName("link_id");

        // install_referrer|login|claim_code|probabilistic|direct_open|none, as text.
        builder.Property(a => a.MatchType)
            .HasColumnName("match_type")
            .IsRequired();

        // numeric(3,2): exactly 1.00 for a deterministic strategy. A probabilistic score is never
        // rounded up into a certainty, so the scale is part of the schema, not of the formatting.
        builder.Property(a => a.Confidence)
            .HasColumnName("confidence")
            .HasColumnType(PostgresConventions.Numeric32)
            .IsRequired();

        builder.Property(a => a.MatchedAt)
            .HasColumnName("matched_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.Property(a => a.WindowSeconds)
            .HasColumnName("window_seconds");

        builder.Property(a => a.Evidence)
            .HasColumnName("evidence")
            .HasColumnType(PostgresConventions.Jsonb)
            .HasDefaultValueSql(PostgresConventions.EmptyJsonObjectDefault)
            .IsRequired();

        builder.HasOne<Install>()
            .WithMany()
            .HasForeignKey(a => a.InstallId)
            .HasConstraintName("fk_attributions_install")
            .OnDelete(DeleteBehavior.Cascade);

        // One installation, one attribution.
        builder.HasIndex(a => a.InstallId)
            .IsUnique()
            .HasDatabaseName("uq_attributions_install");

        // One click may be credited to at most one installation (TC-144). It has to be a partial
        // unique index because click_id is nullable — an unmatched install still gets a row, and
        // several of those must be allowed to coexist.
        //
        // A foreign key to click_events is impossible: that table is partitioned by occurred_at
        // with the composite primary key (occurred_at, id), so there is no unique constraint on
        // click_id alone to point at. This index plus the transactional check in
        // AttributionRepository is what enforces the invariant instead (§B.5.3).
        builder.HasIndex(a => a.ClickId)
            .IsUnique()
            .HasDatabaseName("uq_attributions_click")
            .HasFilter("click_id IS NOT NULL");

        builder.HasIndex(a => new { a.TenantId, a.MatchedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_attributions_tenant_matched");
    }
}
