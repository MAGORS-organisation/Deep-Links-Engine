namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="ClickLoginHint"/> to <c>click_login_hints</c>.</summary>
public sealed class ClickLoginHintConfiguration : IEntityTypeConfiguration<ClickLoginHint>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ClickLoginHint> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("click_login_hints");
        builder.HasKey(h => h.Id).HasName("pk_click_login_hints");

        builder.Property(h => h.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(h => h.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(h => h.LoginKeyHash)
            .HasColumnName("login_key_hash")
            .IsRequired();

        builder.Property(h => h.ClickId)
            .HasColumnName("click_id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(h => h.LinkId)
            .HasColumnName("link_id");

        builder.Property(h => h.DeeplinkPath)
            .HasColumnName("deeplink_path")
            .HasMaxLength(2048);

        builder.Property(h => h.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.Property(h => h.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();

        // Indexed O(1) lookup by tenant and keyed login hash ordered most recent first.
        builder.HasIndex(h => new { h.TenantId, h.LoginKeyHash, h.OccurredAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_click_login_hints_lookup");

        // Retention pruning sweeps expired rows via this index.
        builder.HasIndex(h => h.ExpiresAt)
            .HasDatabaseName("ix_click_login_hints_expires");
    }
}
