namespace Dle.Persistence.Configurations;

/// <summary>Maps <see cref="SlugSequence"/> to the <c>slug_sequences</c> table (ADR-007).</summary>
/// <remarks>
/// One instance wide counter space, deliberately not per tenant: slugs are drawn from a keyed
/// Feistel permutation over a 47 bit counter, and giving every tenant its own space would either
/// leak how many links a neighbour has created or let one tenant exhaust another's range.
/// </remarks>
public sealed class SlugSequenceConfiguration : IEntityTypeConfiguration<SlugSequence>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SlugSequence> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("slug_sequences");
        builder.HasKey(s => s.Id).HasName("pk_slug_sequences");

        builder.Property(s => s.Id)
            .HasColumnName("id")
            .HasDefaultValueSql(PostgresConventions.UuidV7Default);

        builder.Property(s => s.Name)
            .HasColumnName("name")
            .HasDefaultValue("default")
            .IsRequired();

        builder.Property(s => s.BlockStart)
            .HasColumnName("block_start")
            .IsRequired();

        builder.Property(s => s.BlockEnd)
            .HasColumnName("block_end")
            .IsRequired();

        builder.Property(s => s.ClaimedBy)
            .HasColumnName("claimed_by");

        builder.Property(s => s.ClaimedAt)
            .HasColumnName("claimed_at")
            .HasDefaultValueSql(PostgresConventions.NowDefault)
            .IsRequired();

        // A block is never handed out twice. The unique index is the last line of defence behind
        // the advisory lock the allocator takes: a counter value that were reused would produce a
        // duplicate slug, which the Feistel bijection otherwise makes impossible.
        builder.HasIndex(s => new { s.Name, s.BlockStart })
            .IsUnique()
            .HasDatabaseName("uq_slug_sequences_name_start");
    }
}
