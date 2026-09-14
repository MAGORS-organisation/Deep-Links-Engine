namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="Campaign"/> to the <c>campaigns</c> table (§B.5.2).</summary>
public sealed class CampaignConfiguration : IEntityTypeConfiguration<Campaign>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Campaign> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("campaigns");
        builder.HasKey(c => c.Id).HasName("pk_campaigns");

        builder.Property(c => c.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(c => c.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(c => c.Name)
            .HasColumnName("name")
            .IsRequired();

        builder.Property(c => c.Utm)
            .HasColumnName("utm")
            .HasColumnType(PostgresConventions.Jsonb)
            .HasDefaultValueSql(PostgresConventions.EmptyJsonObjectDefault)
            .IsRequired();

        builder.Property(c => c.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .HasConstraintName("fk_campaigns_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => new { c.TenantId, c.Name })
            .HasDatabaseName("ix_campaigns_tenant_name");
    }
}
