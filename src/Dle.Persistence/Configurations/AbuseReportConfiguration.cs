namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="AbuseReport"/> to the <c>abuse_reports</c> table (FR-245, §E.3).</summary>
public sealed class AbuseReportConfiguration : IEntityTypeConfiguration<AbuseReport>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AbuseReport> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("abuse_reports");
        builder.HasKey(r => r.Id).HasName("pk_abuse_reports");

        builder.Property(r => r.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(r => r.LinkId)
            .HasColumnName("link_id")
            .IsRequired();

        builder.Property(r => r.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        // phishing|malware|spam|illegal|copyright|other, as text.
        builder.Property(r => r.Reason)
            .HasColumnName("reason")
            .IsRequired();

        builder.Property(r => r.Details)
            .HasColumnName("details");

        // Hashed so the operator can recognise a repeat reporter and deduplicate without keeping a
        // list of people who reported someone.
        builder.Property(r => r.ReporterEmailHash)
            .HasColumnName("reporter_email_hash");

        // new|triaged|confirmed|rejected|resolved, as text.
        builder.Property(r => r.Status)
            .HasColumnName("status")
            .HasDefaultValue("new")
            .IsRequired();

        builder.Property(r => r.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.Property(r => r.ResolvedAt)
            .HasColumnName("resolved_at");

        builder.Property(r => r.ResolutionNote)
            .HasColumnName("resolution_note");

        builder.HasOne<Link>()
            .WithMany()
            .HasForeignKey(r => r.LinkId)
            .HasConstraintName("fk_abuse_reports_link")
            .OnDelete(DeleteBehavior.Cascade);

        // The triage queue is "open reports, oldest first", because the DSA reaction target runs
        // from submission.
        builder.HasIndex(r => new { r.Status, r.CreatedAt })
            .HasDatabaseName("ix_abuse_reports_queue");

        builder.HasIndex(r => r.LinkId).HasDatabaseName("ix_abuse_reports_link");
    }
}
