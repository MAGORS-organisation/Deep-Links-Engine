using Dle.Domain.Entities;
using Dle.Persistence;
using Dle.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Dle.UnitTests.Persistence;

/// <summary>
/// The <c>text[]</c> columns must stay primitive collections, or containment queries over them
/// cannot be translated to SQL. EF Core 11 maps a <c>List&lt;string&gt;</c> configured with
/// <c>Property()</c> as a scalar, and <c>Contains</c> then fails at run time, which only the
/// PostgreSQL integration suite used to notice. Translation needs no database, so it is pinned here.
/// </summary>
public sealed class ArrayColumnTranslationTests
{
    [Fact]
    public void EveryArrayColumn_IsMappedAsAPrimitiveCollection()
    {
        using DleDbContext db = CreateContext();

        // Every array column in the model, so a new one configured with Property() fails here
        // rather than only in the PostgreSQL integration suite.
        List<IProperty> arrays = [.. db.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.GetColumnType().EndsWith("[]", StringComparison.Ordinal))];

        Assert.Contains(arrays, p => p.DeclaringType.ClrType == typeof(WebhookSubscription) && p.Name == nameof(WebhookSubscription.EventTypes));
        Assert.Contains(arrays, p => p.DeclaringType.ClrType == typeof(Link) && p.Name == nameof(Link.Tags));
        Assert.All(arrays, p => Assert.True(
            p.IsPrimitiveCollection,
            $"{p.DeclaringType.ClrType.Name}.{p.Name} ({p.GetColumnType()}) must be configured with PrimitiveCollection()"));
    }

    [Fact]
    public void WebhookEventTypeContainment_TranslatesToSql()
    {
        using DleDbContext db = CreateContext();
        string eventType = "link.created";

        string sql = db.WebhookSubscriptions
            .IgnoreQueryFilters()
            .Where(s => s.IsActive && s.EventTypes.Contains(eventType))
            .Select(s => s.Id)
            .ToQueryString();

        Assert.Contains("event_types", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkTagContainment_TranslatesToSql()
    {
        using DleDbContext db = CreateContext();
        List<string> required = ["spring", "promo"];

        string sql = db.Links
            .IgnoreQueryFilters()
            .Where(l => required.All(tag => l.Tags.Contains(tag)))
            .ToQueryString();

        Assert.Contains("tags", sql, StringComparison.Ordinal);
    }

    private static DleDbContext CreateContext()
    {
        // Translation only: the connection string is parsed, never opened.
        DbContextOptions<DleDbContext> options = new DbContextOptionsBuilder<DleDbContext>()
            .UseNpgsql("Host=localhost;Database=dle_unit_tests")
            .Options;
        return new DleDbContext(options, new AmbientTenantContext(), TimeProvider.System);
    }
}
