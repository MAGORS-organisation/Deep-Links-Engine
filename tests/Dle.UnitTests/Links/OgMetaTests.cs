using Dle.Domain.Links;
using Xunit;

namespace Dle.UnitTests.Links;

/// <summary>
/// Open Graph metadata is what a crawler sees (ADR-009 row 1, TC-106). A blank field must fall back
/// to the domain default rather than render an empty preview card.
/// </summary>
public sealed class OgMetaTests
{
    [Fact]
    public void Empty_CarriesTheDocumentedDefaults()
    {
        Assert.Equal(OgMeta.DefaultType, OgMeta.Empty.Type);
        Assert.Equal(OgMeta.DefaultTwitterCard, OgMeta.Empty.TwitterCard);
        Assert.Null(OgMeta.Empty.Title);
    }

    [Fact]
    public void MergeWith_KeepsTheOwnValueWhenPresent()
    {
        OgMeta primary = new() { Title = "Product", Description = "Own description" };
        OgMeta fallback = new() { Title = "Domain default", Description = "Domain description" };

        OgMeta merged = primary.MergeWith(fallback);

        Assert.Equal("Product", merged.Title);
        Assert.Equal("Own description", merged.Description);
    }

    [Fact]
    public void MergeWith_TakesTheFallbackForMissingFields()
    {
        OgMeta primary = new() { Title = "Product" };
        OgMeta fallback = new() { Description = "Domain description", ImageUrl = "https://cdn.example.com/a.png" };

        OgMeta merged = primary.MergeWith(fallback);

        Assert.Equal("Product", merged.Title);
        Assert.Equal("Domain description", merged.Description);
        Assert.Equal("https://cdn.example.com/a.png", merged.ImageUrl);
    }

    [Fact]
    public void MergeWith_TreatsWhitespaceAsMissing()
    {
        OgMeta primary = new() { Title = "   " };
        OgMeta fallback = new() { Title = "Domain default" };

        Assert.Equal("Domain default", primary.MergeWith(fallback).Title);
    }

    [Fact]
    public void MergeWith_NullFallback_StillYieldsTheTypeDefaults()
    {
        OgMeta merged = new OgMeta { Title = "Product" }.MergeWith(null);

        Assert.Equal(OgMeta.DefaultType, merged.Type);
        Assert.Equal(OgMeta.DefaultTwitterCard, merged.TwitterCard);
        Assert.Null(merged.Description);
    }

    [Fact]
    public void MergeWith_BothSidesBlank_LeavesTheFieldNullRatherThanEmpty()
    {
        OgMeta merged = new OgMeta { Description = "  " }.MergeWith(new OgMeta { Description = string.Empty });

        Assert.Null(merged.Description);
    }
}
