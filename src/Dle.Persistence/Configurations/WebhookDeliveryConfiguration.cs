namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="WebhookDelivery"/> to the <c>webhook_deliveries</c> table (FR-204).</summary>
public sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("webhook_deliveries");
        builder.HasKey(d => d.Id).HasName("pk_webhook_deliveries");

        builder.Property(d => d.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(d => d.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(d => d.SubscriptionId)
            .HasColumnName("subscription_id")
            .IsRequired();

        builder.Property(d => d.EventType)
            .HasColumnName("event_type")
            .IsRequired();

        // Stored, not rebuilt on retry: the signature covers these exact bytes, and regenerating
        // the JSON could change the property order and produce a signature the customer's verifier
        // rejects.
        builder.Property(d => d.Payload)
            .HasColumnName("payload")
            .HasColumnType(PostgresConventions.Jsonb)
            .HasDefaultValueSql(PostgresConventions.EmptyJsonObjectDefault)
            .IsRequired();

        builder.Property(d => d.Attempt)
            .HasColumnName("attempt")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(d => d.Status)
            .HasColumnName("status")
            .HasDefaultValue("pending")
            .IsRequired();

        builder.Property(d => d.NextAttemptAt)
            .HasColumnName("next_attempt_at");

        builder.Property(d => d.LastError)
            .HasColumnName("last_error");

        builder.Property(d => d.ResponseCode)
            .HasColumnName("response_code");

        builder.Property(d => d.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.Property(d => d.DeliveredAt)
            .HasColumnName("delivered_at");

        builder.HasOne<WebhookSubscription>()
            .WithMany()
            .HasForeignKey(d => d.SubscriptionId)
            .HasConstraintName("fk_webhook_deliveries_subscription")
            .OnDelete(DeleteBehavior.Cascade);

        // The delivery worker polls exactly this: due, still pending, oldest first. Partial, so the
        // index stays the size of the backlog rather than the size of the history.
        builder.HasIndex(d => new { d.Status, d.NextAttemptAt })
            .HasDatabaseName("ix_webhook_deliveries_due")
            .HasFilter("status = 'pending'");

        builder.HasIndex(d => new { d.TenantId, d.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_webhook_deliveries_tenant_created");
    }
}
