namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="Tenant"/> to the <c>tenants</c> table (§B.5.2).</summary>
public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tenants");
        builder.HasKey(t => t.Id).HasName("pk_tenants");

        builder.Property(t => t.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        // citext, so "Acme" and "acme" are the same tenant and the unique index enforces it.
        builder.Property(t => t.Slug)
            .HasColumnName("slug")
            .HasColumnType(PostgresConventions.CiText)
            .IsRequired();

        builder.Property(t => t.Name)
            .HasColumnName("name")
            .IsRequired();

        // active|suspended|deleted — text, not an integer, so the value is readable in the
        // database and survives a reordering of the enum (shared kernel §12).
        builder.Property(t => t.Status)
            .HasColumnName("status")
            .HasDefaultValue("active")
            .IsRequired();

        // full|aggregate_only|off
        builder.Property(t => t.ConsentMode)
            .HasColumnName("consent_mode")
            .HasDefaultValue("aggregate_only")
            .IsRequired();

        builder.Property(t => t.Settings)
            .HasColumnName("settings")
            .HasColumnType(PostgresConventions.Jsonb)
            .HasDefaultValueSql(PostgresConventions.EmptyJsonObjectDefault)
            .IsRequired();

        builder.Property(t => t.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        builder.HasIndex(t => t.Slug).IsUnique().HasDatabaseName("uq_tenants_slug");
    }
}
