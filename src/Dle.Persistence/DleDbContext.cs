using System.Linq.Expressions;

using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Dle.Persistence;

/// <summary>
/// The control-plane database context (ADR-004). Owns the schema for every table in §B.5.2 and,
/// through its migrations, for the partitioned click stream of §B.5.3 as well.
/// </summary>
/// <remarks>
/// <para>
/// EF Core is deliberately confined to the control plane. The resolve path never sees this class:
/// it runs one hand written statement through Dapper, because change tracking and entity
/// materialization cost hundreds of microseconds out of an eight millisecond budget and close the
/// door on ahead of time compilation (ADR-004). What EF Core owns here is the part where
/// productivity beats microseconds — CRUD, relationships, and above all the migrations, which are
/// the single source of truth about the schema that the Dapper queries then depend on.
/// </para>
/// <para>
/// Two named query filters are attached to the model. <see cref="DleQueryFilters.Tenant"/> scopes
/// every tenant owned entity type, and <see cref="DleQueryFilters.SoftDelete"/> hides withdrawn
/// rows. They are applied centrally in <see cref="OnModelCreating"/> rather than in the individual
/// entity configurations, and the model build fails if any mapped entity type is neither covered
/// nor listed as deliberately exempt — so adding an entity forces the decision instead of allowing
/// it to be forgotten (FR-241, T-09).
/// </para>
/// </remarks>
public sealed class DleDbContext : DbContext
{
    /// <summary>Name of the property that carries the owning tenant on tenant scoped entities.</summary>
    private const string TenantIdPropertyName = "TenantId";

    /// <summary>Name of the creation timestamp property stamped on insert.</summary>
    private const string CreatedAtPropertyName = "CreatedAt";

    /// <summary>Name of the modification timestamp property stamped on insert and update.</summary>
    private const string UpdatedAtPropertyName = "UpdatedAt";

    /// <summary>Status value of a tenant that has been deleted but is kept for referential history.</summary>
    private const string DeletedTenantStatus = "deleted";

    /// <summary>
    /// Entity types that carry no tenant column, together with the reason. Every mapped entity type
    /// must be here or must be tenant filtered; the model build enforces it.
    /// </summary>
    private static readonly Dictionary<Type, string> TenantExemptEntityTypes = new()
    {
        [typeof(AppDomainEntity)] =
            "Join table of apps and domains (§B.5.2 has no tenant_id on it). Both sides are " +
            "tenant filtered, and AppRepository only ever reaches it through them.",
        [typeof(LinkVersion)] =
            "History rows hang off links.id and carry no tenant column. LinkRepository always " +
            "joins through the tenant filtered link.",
        [typeof(DomainVerification)] =
            "Check results hang off domains.id and carry no tenant column. DomainRepository " +
            "always joins through the tenant filtered domain.",
        [typeof(SlugSequence)] =
            "The slug counter is one instance wide space (ADR-007); tenants must not be able to " +
            "influence, observe or exhaust each other's slugs by having their own.",
    };

    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _timeProvider;
    private int _crossTenantDepth;

    /// <summary>Creates the context.</summary>
    /// <param name="options">Provider and connection configuration.</param>
    /// <param name="tenantContext">Source of the tenant every query and write is scoped to.</param>
    /// <param name="timeProvider">Clock used to stamp creation and modification instants.</param>
    public DleDbContext(
        DbContextOptions<DleDbContext> options,
        ITenantContext tenantContext,
        TimeProvider timeProvider)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _tenantContext = tenantContext;
        _timeProvider = timeProvider;
    }

    /// <summary>Tenants: the unit of ownership and of isolation. Table <c>tenants</c>.</summary>
    public DbSet<Tenant> Tenants => Set<Tenant>();

    /// <summary>Hosts that serve links, with their verification state. Table <c>domains</c>.</summary>
    public DbSet<LinkDomain> Domains => Set<LinkDomain>();

    /// <summary>Registered mobile applications. Table <c>apps</c>.</summary>
    public DbSet<App> Apps => Set<App>();

    /// <summary>Application to domain pairings. Table <c>app_domains</c>.</summary>
    public DbSet<AppDomainEntity> AppDomains => Set<AppDomainEntity>();

    /// <summary>Campaigns grouping links and supplying UTM defaults. Table <c>campaigns</c>.</summary>
    public DbSet<Campaign> Campaigns => Set<Campaign>();

    /// <summary>Short links. Table <c>links</c>.</summary>
    public DbSet<Link> Links => Set<Link>();

    /// <summary>Historical revisions of links. Table <c>link_versions</c>.</summary>
    public DbSet<LinkVersion> LinkVersions => Set<LinkVersion>();

    /// <summary>Control-plane API keys. Table <c>api_keys</c>.</summary>
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    /// <summary>Keys shipped inside customer applications. Table <c>sdk_keys</c>.</summary>
    public DbSet<SdkKey> SdkKeys => Set<SdkKey>();

    /// <summary>Webhook endpoints. Table <c>webhook_subscriptions</c>.</summary>
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();

    /// <summary>Webhook delivery attempts and their retry state. Table <c>webhook_deliveries</c>.</summary>
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    /// <summary>Application installations reported by the SDK. Table <c>installs</c>.</summary>
    public DbSet<Install> Installs => Set<Install>();

    /// <summary>Attribution decisions. Table <c>attributions</c>.</summary>
    public DbSet<AttributionRecord> Attributions => Set<AttributionRecord>();

    /// <summary>Claim codes issued on interstitial pages. Table <c>claim_codes</c>.</summary>
    public DbSet<ClaimCodeRecord> ClaimCodes => Set<ClaimCodeRecord>();

    /// <summary>Abuse reports. Table <c>abuse_reports</c>.</summary>
    public DbSet<AbuseReport> AbuseReports => Set<AbuseReport>();

    /// <summary>Administrative audit trail. Table <c>audit_log</c>.</summary>
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    /// <summary>The signing key ring. Table <c>signing_keys</c>.</summary>
    public DbSet<SigningKeyRecord> SigningKeys => Set<SigningKeyRecord>();

    /// <summary>Recorded domain verification runs. Table <c>domain_verifications</c>.</summary>
    public DbSet<DomainVerification> DomainVerifications => Set<DomainVerification>();

    /// <summary>Claimed blocks of the slug counter. Table <c>slug_sequences</c>.</summary>
    public DbSet<SlugSequence> SlugSequences => Set<SlugSequence>();

    /// <summary>Replayable responses for idempotent requests. Table <c>idempotency_records</c>.</summary>
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    /// <summary>Whether a cross-tenant scope is currently open on this context.</summary>
    public bool IsCrossTenantScope => _crossTenantDepth > 0;

    /// <summary>
    /// The value the tenant query filter compares against.
    /// </summary>
    /// <remarks>
    /// Outside a cross-tenant scope this throws when no tenant has been established, which is the
    /// whole point: a query that forgot the tenant cannot run at all. Inside one it yields
    /// <see cref="Guid.Empty"/>, a value no row carries, so a query that forgets to drop the filter
    /// by name fails closed rather than open.
    /// </remarks>
    internal Guid TenantFilterValue => IsCrossTenantScope ? Guid.Empty : _tenantContext.RequiredTenantId;

    /// <summary>
    /// Opens a window in which queries on this context may drop the tenant filter by name.
    /// </summary>
    /// <param name="reason">Why this unit of work legitimately spans tenants.</param>
    /// <returns>A handle that closes the window when disposed.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is empty.</exception>
    /// <remarks>
    /// Scopes nest, so a cross-tenant worker may call into code that opens one of its own. Nothing
    /// widens automatically: the query still has to call
    /// <see cref="DleQueryableExtensions.AcrossTenants{TEntity}"/>.
    /// </remarks>
    public CrossTenantScope BeginCrossTenantScope(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        _crossTenantDepth++;
        return new CrossTenantScope(this, reason);
    }

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyStamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyStamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        // citext gives case insensitive slugs and hosts without a lower() functional index on every
        // lookup, which matters because the resolve index has to stay usable for an index only scan
        // (§B.5.2). pg_partman is deliberately not declared here: it is optional, and a missing
        // extension must degrade to a warning rather than fail the migration.
        modelBuilder.HasPostgresExtension("citext");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DleDbContext).Assembly);

        ApplyTenantFilters(modelBuilder);
        ApplySoftDeleteFilters(modelBuilder);
    }

    /// <summary>Closes one level of cross-tenant scope. Called by <see cref="CrossTenantScope"/>.</summary>
    internal void EndCrossTenantScope()
    {
        if (_crossTenantDepth > 0)
        {
            _crossTenantDepth--;
        }
    }

    /// <summary>
    /// Attaches the tenant filter to every tenant owned entity type, then proves that nothing was
    /// missed.
    /// </summary>
    /// <param name="modelBuilder">The model under construction.</param>
    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        HashSet<Type> filtered = [];

        void Scope<TEntity>(Expression<Func<TEntity, bool>> filter)
            where TEntity : class
        {
            modelBuilder.Entity<TEntity>().HasQueryFilter(DleQueryFilters.Tenant, filter);
            filtered.Add(typeof(TEntity));
        }

        Scope<Tenant>(t => t.Id == TenantFilterValue);
        Scope<LinkDomain>(d => d.TenantId == TenantFilterValue);
        Scope<App>(a => a.TenantId == TenantFilterValue);
        Scope<Campaign>(c => c.TenantId == TenantFilterValue);
        Scope<Link>(l => l.TenantId == TenantFilterValue);
        Scope<ApiKey>(k => k.TenantId == TenantFilterValue);
        Scope<SdkKey>(k => k.TenantId == TenantFilterValue);
        Scope<WebhookSubscription>(s => s.TenantId == TenantFilterValue);
        Scope<WebhookDelivery>(d => d.TenantId == TenantFilterValue);
        Scope<Install>(i => i.TenantId == TenantFilterValue);
        Scope<AttributionRecord>(a => a.TenantId == TenantFilterValue);
        Scope<ClaimCodeRecord>(c => c.TenantId == TenantFilterValue);
        Scope<AbuseReport>(r => r.TenantId == TenantFilterValue);
        Scope<AuditLogEntry>(e => e.TenantId == TenantFilterValue);
        Scope<IdempotencyRecord>(r => r.TenantId == TenantFilterValue);

        // A signing key with no tenant is the instance wide key every tenant verifies against
        // (§E.4.2), so the filter admits it alongside the tenant's own keys.
        Scope<SigningKeyRecord>(k => k.TenantId == null || k.TenantId == TenantFilterValue);

        VerifyEveryEntityTypeIsClassified(modelBuilder, filtered);
    }

    /// <summary>
    /// Attaches the soft delete filter to the entity types that have a withdrawal column.
    /// </summary>
    /// <param name="modelBuilder">The model under construction.</param>
    /// <remarks>
    /// A quarantined link is not deleted — the edge still has to find it to answer 410 rather than
    /// 404 (TC-103) — but it has no business appearing in the tenant's ordinary link list. That is
    /// exactly the distinction a second, separately nameable filter buys: the control plane hides it
    /// by default and asks for it back with
    /// <see cref="DleQueryableExtensions.IncludeSoftDeleted{TEntity}"/> when the operator wants to
    /// review or release it, while the tenant filter stays on throughout.
    /// </remarks>
    private static void ApplySoftDeleteFilters(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>()
            .HasQueryFilter(DleQueryFilters.SoftDelete, t => t.Status != DeletedTenantStatus);
        modelBuilder.Entity<Link>()
            .HasQueryFilter(DleQueryFilters.SoftDelete, l => l.QuarantinedAt == null);
        modelBuilder.Entity<ApiKey>()
            .HasQueryFilter(DleQueryFilters.SoftDelete, k => k.RevokedAt == null);
    }

    /// <summary>
    /// Fails the model build if a mapped entity type is neither tenant filtered nor listed in
    /// <see cref="TenantExemptEntityTypes"/> with a reason.
    /// </summary>
    /// <param name="modelBuilder">The model under construction.</param>
    /// <param name="filtered">The entity types that were just given a tenant filter.</param>
    /// <exception cref="InvalidOperationException">An entity type is unclassified, is classified
    /// twice, or is exempt while carrying a tenant column after all.</exception>
    /// <remarks>
    /// This is what turns "remember to scope your queries" into something the compiler's runtime
    /// equivalent enforces. Adding an entity to the model without deciding whether it belongs to a
    /// tenant now fails on the first use of the context, with a message naming the type, instead of
    /// producing a query that quietly returns every tenant's rows.
    /// </remarks>
    private static void VerifyEveryEntityTypeIsClassified(ModelBuilder modelBuilder, HashSet<Type> filtered)
    {
        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            Type clrType = entityType.ClrType;
            bool isFiltered = filtered.Contains(clrType);
            bool isExempt = TenantExemptEntityTypes.ContainsKey(clrType);

            if (isFiltered && isExempt)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Entity type {clrType.Name} is both tenant filtered and listed as tenant " +
                    $"exempt. Remove one of the two."));
            }

            if (isExempt && entityType.FindProperty(TenantIdPropertyName) is not null)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Entity type {clrType.Name} is listed as tenant exempt but carries a " +
                    $"{TenantIdPropertyName} property. Remove it from " +
                    $"DleDbContext.TenantExemptEntityTypes and give it a " +
                    $"'{DleQueryFilters.Tenant}' query filter."));
            }

            if (!isFiltered && !isExempt)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Entity type {clrType.Name} is neither tenant filtered nor listed in " +
                    $"DleDbContext.TenantExemptEntityTypes. Either give it a " +
                    $"'{DleQueryFilters.Tenant}' query filter in " +
                    $"DleDbContext.ApplyTenantFilters, or record there why it is instance wide " +
                    $"(FR-241, T-09)."));
            }
        }
    }

    /// <summary>
    /// Fills in identifiers, timestamps and the owning tenant, and refuses a write that belongs to
    /// another tenant.
    /// </summary>
    private void ApplyStamps()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        foreach (EntityEntry entry in ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    StampIdentity(entry, now);
                    StampTenant(entry);
                    SetTimestamp(entry, CreatedAtPropertyName, now, onlyWhenUnset: true);
                    SetTimestamp(entry, UpdatedAtPropertyName, now, onlyWhenUnset: false);
                    break;

                case EntityState.Modified:
                    GuardTenant(entry);
                    SetTimestamp(entry, UpdatedAtPropertyName, now, onlyWhenUnset: false);
                    break;

                case EntityState.Deleted:
                    GuardTenant(entry);
                    break;

                case EntityState.Detached:
                case EntityState.Unchanged:
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Gives a new row a time ordered identifier on the client, so a PostgreSQL 16 deployment gets
    /// the same key shape as a PostgreSQL 18 one.
    /// </summary>
    /// <param name="entry">The entry being inserted.</param>
    /// <param name="now">The current instant.</param>
    /// <remarks>
    /// The columns also carry a <c>dle_uuidv7()</c> default so that inserts made outside EF Core —
    /// a bulk import, a psql session — are equally well ordered. Stamping here as well means EF
    /// Core never has to read the value back, and means the identifier is known before the
    /// transaction commits.
    /// </remarks>
    private static void StampIdentity(EntityEntry entry, DateTimeOffset now)
    {
        IKey? primaryKey = entry.Metadata.FindPrimaryKey();
        if (primaryKey is null)
        {
            return;
        }

        foreach (IProperty keyProperty in primaryKey.Properties)
        {
            if (keyProperty.ClrType != typeof(Guid))
            {
                continue;
            }

            // A composite key can contain the tenant column (idempotency_records is keyed by
            // tenant, endpoint and key). That column is an owner reference, never an identity:
            // minting a fresh identifier for it would silently file the row under a tenant that
            // does not exist and then trip the cross-tenant guard on the very next line.
            if (string.Equals(keyProperty.Name, TenantIdPropertyName, StringComparison.Ordinal))
            {
                continue;
            }

            PropertyEntry property = entry.Property(keyProperty.Name);
            if (property.CurrentValue is Guid current && current == Guid.Empty)
            {
                property.CurrentValue = Guid.CreateVersion7(now);
            }
        }
    }

    /// <summary>Fills in the owning tenant on insert, or rejects a foreign one.</summary>
    /// <param name="entry">The entry being inserted.</param>
    private void StampTenant(EntityEntry entry)
    {
        if (entry.Metadata.FindProperty(TenantIdPropertyName) is not IProperty tenantProperty)
        {
            return;
        }

        PropertyEntry property = entry.Property(TenantIdPropertyName);

        if (property.CurrentValue is Guid assigned && assigned != Guid.Empty)
        {
            GuardTenantValue(entry, assigned);
            return;
        }

        // A signing key with no tenant is instance wide on purpose; nothing else may be.
        if (tenantProperty.IsNullable && property.CurrentValue is null)
        {
            return;
        }

        property.CurrentValue = _tenantContext.RequiredTenantId;
    }

    /// <summary>Rejects an update or a delete aimed at another tenant's row.</summary>
    /// <param name="entry">The entry being written.</param>
    private void GuardTenant(EntityEntry entry)
    {
        if (entry.Metadata.FindProperty(TenantIdPropertyName) is null)
        {
            return;
        }

        if (entry.Property(TenantIdPropertyName).CurrentValue is Guid assigned && assigned != Guid.Empty)
        {
            GuardTenantValue(entry, assigned);
        }
    }

    /// <summary>Compares a row's tenant against the one in scope.</summary>
    /// <param name="entry">The entry being written.</param>
    /// <param name="assigned">The tenant the row carries.</param>
    /// <exception cref="CrossTenantWriteException">The row belongs to another tenant.</exception>
    private void GuardTenantValue(EntityEntry entry, Guid assigned)
    {
        if (IsCrossTenantScope)
        {
            return;
        }

        if (_tenantContext.TenantId is Guid scoped && scoped != assigned)
        {
            throw new CrossTenantWriteException(entry.Metadata.DisplayName(), scoped, assigned);
        }
    }

    /// <summary>Writes a timestamp property when the entity type has one.</summary>
    /// <param name="entry">The entry being written.</param>
    /// <param name="propertyName">Name of the timestamp property.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="onlyWhenUnset">Whether an explicitly supplied value must be preserved.</param>
    private static void SetTimestamp(
        EntityEntry entry,
        string propertyName,
        DateTimeOffset now,
        bool onlyWhenUnset)
    {
        if (entry.Metadata.FindProperty(propertyName) is not IProperty metadata
            || metadata.ClrType != typeof(DateTimeOffset))
        {
            return;
        }

        PropertyEntry property = entry.Property(propertyName);
        if (onlyWhenUnset && property.CurrentValue is DateTimeOffset existing && existing != default)
        {
            return;
        }

        property.CurrentValue = now;
    }
}
