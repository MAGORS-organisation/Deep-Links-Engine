namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="WebhookSubscription"/> to the <c>webhook_subscriptions</c> table (FR-204).</summary>
public sealed class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<WebhookSubscription>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WebhookSubscription> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("webhook_subscriptions");
        builder.HasKey(s => s.Id).HasName("pk_webhook_subscriptions");

        builder.Property(s => s.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(s => s.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(s => s.Url)
            .HasColumnName("url")
            .IsRequired();

        builder.Property(s => s.SecretEncrypted)
            .HasColumnName("secret_encrypted")
            .IsRequired();

        builder.Property(s => s.EventTypes)
            .HasColumnName("event_types")
            .HasColumnType(PostgresConventions.TextArray)
            .HasDefaultValueSql(PostgresConventions.EmptyTextArrayDefault)
            .IsRequired();

        builder.Property(s => s.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(s => s.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(s => s.TenantId)
            .HasConstraintName("fk_webhook_subscriptions_tenant")
            .OnDelete(DeleteBehavior.Cascade);

        // The dispatcher asks "who subscribes to this event type in this tenant", which is a
        // containment test over the array — a GIN index, not a btree.
        builder.HasIndex(s => s.TenantId).HasDatabaseName("ix_webhook_subscriptions_tenant");
        builder.HasIndex(s => s.EventTypes)
            .HasMethod("gin")
            .HasDatabaseName("ix_webhook_subscriptions_event_types");
    }
}
