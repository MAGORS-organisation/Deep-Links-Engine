namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="Link"/> to the <c>links</c> table (§B.5.2).</summary>
/// <remarks>
/// This is the only table on the resolve path, so its indexes are part of the latency budget rather
/// than an afterthought (§B.6.1).
/// </remarks>
public sealed class LinkConfiguration : IEntityTypeConfiguration<Link>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Link> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("links");
        builder.HasKey(l => l.Id).HasName("pk_links");

        // A Snowflake identifier minted by the application, never a sequence: the identifier has to
        // be known before the insert so that the response and the audit entry can be written
        // without a round trip.
        builder.Property(l => l.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(l => l.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(l => l.DomainId)
            .HasColumnName("domain_id")
            .IsRequired();

        builder.Property(l => l.Slug)
            .HasColumnName("slug")
            .HasColumnType(PostgresConventions.CiText)
            .IsRequired();

        builder.Property(l => l.Title)
            .HasColumnName("title");

        builder.Property(l => l.Description)
            .HasColumnName("description");

        builder.Property(l => l.TargetUrl)
            .HasColumnName("target_url")
            .IsRequired();

        builder.Property(l => l.DeeplinkPath)
            .HasColumnName("deeplink_path");

        builder.Property(l => l.RoutingRules)
            .HasColumnName("routing_rules")
            .HasColumnType(PostgresConventions.Jsonb)
            .HasDefaultValueSql(PostgresConventions.EmptyJsonArrayDefault)
            .IsRequired();

        builder.Property(l => l.OgMeta)
            .HasColumnName("og_meta")
            .HasColumnType(PostgresConventions.Jsonb)
            .HasDefaultValueSql(PostgresConventions.EmptyJsonObjectDefault)
            .IsRequired();

        builder.Property(l => l.Utm)
            .HasColumnName("utm")
            .HasColumnType(PostgresConventions.Jsonb)
            .HasDefaultValueSql(PostgresConventions.EmptyJsonObjectDefault)
            .IsRequired();

        builder.Property(l => l.CampaignId)
            .HasColumnName("campaign_id");

        builder.Property(l => l.Tags)
            .HasColumnName("tags")
            .HasColumnType(PostgresConventions.TextArray)
            .HasDefaultValueSql(PostgresConventions.EmptyTextArrayDefault)
            .IsRequired();

        // HasDefaultValue on a bool makes EF Core treat the CLR default (false) as "not set" and
        // omit the column, so a link created with is_active = false would be inserted active. With
        // true as the sentinel, false is written and true - the only value the store default can
        // produce anyway - is what gets omitted.
        builder.Property(l => l.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .HasSentinel(true)
            .IsRequired();

        builder.Property(l => l.StartsAt)
            .HasColumnName("starts_at");

        builder.Property(l => l.ExpiresAt)
            .HasColumnName("expires_at");

        builder.Property(l => l.ExpiredUrl)
            .HasColumnName("expired_url");

        builder.Property(l => l.QuarantinedAt)
            .HasColumnName("quarantined_at");

        builder.Property(l => l.CreatedBy)
            .HasColumnName("created_by");

        builder.Property(l => l.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.Property(l => l.UpdatedAt)
            .HasColumnName("updated_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        // The version doubles as the optimistic-concurrency token: every write that changes the
        // row bumps it and carries "WHERE version = <read>". A stale edit - one prepared from a
        // read that predates a concurrent edit or an abuse quarantine - is rejected instead of
        // silently reversing the newer write (T-09, TC-103).
        builder.Property(l => l.Version)
            .HasColumnName("version")
            .HasDefaultValue(1)
            .IsConcurrencyToken()
            .IsRequired();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(l => l.TenantId)
            .HasConstraintName("fk_links_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<LinkDomain>()
            .WithMany()
            .HasForeignKey(l => l.DomainId)
            .HasConstraintName("fk_links_domain")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => new { l.DomainId, l.Slug }, "uq_links_domain_slug")
            .IsUnique()
            .HasDatabaseName("uq_links_domain_slug");

        // The critical index for the hot path: covering, so the resolve query is answered from the
        // index alone and never touches the heap (§B.5.2).
        //
        // It is deliberately NOT a partial index. Two reasons, and both matter:
        //
        //   1. The resolve query has no predicate on quarantined_at — it selects on
        //      (domain_id, slug) and nothing else — so the planner could not prove a
        //      "WHERE quarantined_at IS NULL" index applicable and would simply ignore it.
        //   2. A quarantined link must still be found. Abuse handling answers it with 410 Gone and
        //      an explanation, not with 404: hiding the row would turn a withdrawn link into an
        //      unknown one and destroy the distinction the DSA notice-and-action flow depends on
        //      (TC-103, §E.3).
        //
        // Index-only scans additionally need a well maintained visibility map, which is why the
        // migration lowers autovacuum_vacuum_scale_factor on this table to 0.02.
        //
        // The list is §B.5.2's plus the five columns the resolve statement in DapperLinkStore also
        // reads to build a LinkSnapshot: id (the click event's link), utm (appended to the target),
        // title (interstitial and Open Graph), campaign_id (attribution) and expired_url (the
        // 302-to-a-farewell-page of TC-104). Covering means every column the statement touches;
        // one outside the list sends the whole lookup back to the heap, and the integration suite
        // checks the plan of that exact statement rather than the spec's shorter one.
        builder.HasIndex(l => new { l.DomainId, l.Slug }, "ix_links_resolve")
            .HasDatabaseName("ix_links_resolve")
            .IncludeProperties(l => new
            {
                l.TargetUrl,
                l.DeeplinkPath,
                l.RoutingRules,
                l.OgMeta,
                l.IsActive,
                l.StartsAt,
                l.ExpiresAt,
                l.QuarantinedAt,
                l.TenantId,
                l.Id,
                l.Utm,
                l.Title,
                l.CampaignId,
                l.ExpiredUrl,
            });

        // Control-plane listing: newest first within a tenant.
        builder.HasIndex(l => new { l.TenantId, l.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_links_tenant_created");

        builder.HasIndex(l => new { l.TenantId, l.CampaignId })
            .HasDatabaseName("ix_links_tenant_campaign");

        // Tag search (FR-109) is containment over a text[], which is a GIN index, not a btree.
        builder.HasIndex(l => l.Tags)
            .HasMethod("gin")
            .HasDatabaseName("ix_links_tags");
    }
}
