using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>Removing translations whose entity was deleted without passing through SaveChanges.</summary>
public sealed class OrphanedTranslationTests
{
    private static Dictionary<string, string?> Name(string value) => new() { ["Name"] = value };

    /// <summary>KEEP and GONE, each with en + fr names, and a category with an en name.</summary>
    private static async Task SeedAsync(Shop shop)
    {
        await using var scope = shop.Scope();

        foreach (var sku in new[] { "KEEP", "GONE" })
        {
            var product = new ShopProduct { Sku = sku };
            scope.Translations.Stage(product, "en", Name(sku.ToLowerInvariant()));
            scope.Translations.Stage(product, "fr", Name(sku.ToLowerInvariant() + " (fr)"));
            scope.Db.Products.Add(product);
        }

        var category = new ShopCategory { Slug = "books" };
        scope.Translations.Stage(category, "en", Name("Books"));
        scope.Db.Categories.Add(category);

        await scope.Db.SaveChangesAsync();
    }

    private static async Task BulkDeleteAsync(Shop shop, string sku)
    {
        await using var scope = shop.Scope();
        await scope.Db.Products.Where(p => p.Sku == sku).ExecuteDeleteAsync();
    }

    private static async Task<List<string?>> StoredNamesAsync(Shop shop) =>
        (await shop.StoredTranslationsAsync()).Select(t => t.Value).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task BulkDelete_LeavesOrphans_WhichThePurgeRemoves_AndNothingElse()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await BulkDeleteAsync(shop, "GONE");

        Assert.Equal(5, (await shop.StoredTranslationsAsync()).Count);   // the interceptor never saw the delete

        await using var scope = shop.Scope();
        Assert.Equal(2, await scope.Db.PurgeOrphanedTranslationsAsync());

        Assert.Equal(["Books", "keep", "keep (fr)"], await StoredNamesAsync(shop));
    }

    [Fact]
    public async Task Purge_IsIdempotent_AndReportsZeroWhenThereIsNothingToDo()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);

        await using var scope = shop.Scope();
        Assert.Equal(0, await scope.Db.PurgeOrphanedTranslationsAsync());

        await BulkDeleteAsync(shop, "GONE");
        Assert.Equal(2, await scope.Db.PurgeOrphanedTranslationsAsync());
        Assert.Equal(0, await scope.Db.PurgeOrphanedTranslationsAsync());
    }

    [Fact]
    public async Task Purge_Sync_DoesTheSame()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await BulkDeleteAsync(shop, "GONE");

        await using var scope = shop.Scope();
        Assert.Equal(2, scope.Db.PurgeOrphanedTranslations());
        Assert.Equal(3, (await shop.StoredTranslationsAsync()).Count);
    }

    [Fact]
    public async Task GuidKeys_LiveRowsSurvive()
    {
        // The dangerous direction: a key compared in the wrong case would make every live row an
        // "orphan". Guids are stored lower case; the comparison must be too.
        await using var shop = await Shop.StartAsync();
        var kept = Guid.NewGuid();
        var gone = Guid.NewGuid();

        await using (var write = await shop.ScopeInAsync("en"))
        {
            write.Db.Documents.AddRange(new ShopDocument { Id = kept, Title = "Kept" }, new ShopDocument { Id = gone, Title = "Gone" });
            await write.Db.SaveChangesAsync();
        }

        await using (var delete = shop.Scope())
            await delete.Db.Documents.Where(d => d.Id == gone).ExecuteDeleteAsync();

        await using var scope = shop.Scope();
        Assert.Equal(1, await scope.Db.PurgeOrphanedTranslationsAsync());

        var remaining = Assert.Single(await shop.StoredTranslationsAsync());
        Assert.Equal((kept.ToString("D"), "Kept"), (remaining.EntityId, remaining.Value));
    }

    [Fact]
    public async Task RowsForAnEntityNameTheModelDoesNotKnow_AreLeftAlone()
    {
        await using var shop = await Shop.StartAsync();

        await using (var write = shop.Scope())
        {
            // Another context's entity, or a class renamed without fixing its entity name.
            write.Db.Translations.Add(new Translation
            {
                EntityName = "LegacyProduct", EntityId = "1", PropertyName = "Name", LanguageCode = "en", Value = "Old"
            });
            await write.Db.SaveChangesAsync();
        }

        await using var scope = shop.Scope();
        Assert.Equal(0, await scope.Db.PurgeOrphanedTranslationsAsync());
        Assert.Single(await shop.StoredTranslationsAsync());
    }

    [Fact]
    public async Task BaseLanguageMode_PurgesTheTranslationRows()
    {
        await using var shop = await BaseShop.StartAsync();
        var keep = await shop.CreateProductAsync("KEEP", "Keep");
        var gone = await shop.CreateProductAsync("GONE", "Gone");

        foreach (var id in new[] { keep, gone })
        {
            await using var write = await shop.ScopeInAsync("ar");
            (await write.Db.Products.SingleAsync(p => p.Id == id)).Name = "عربي";
            await write.Db.SaveChangesAsync();
        }

        await using (var delete = shop.Scope())
            await delete.Db.Products.Where(p => p.Id == gone).ExecuteDeleteAsync();

        await using var scope = shop.Scope();
        Assert.Equal(1, await scope.Db.PurgeOrphanedTranslationsAsync());

        var remaining = Assert.Single(await shop.StoredTranslationsAsync());
        Assert.Equal(keep.ToString(System.Globalization.CultureInfo.InvariantCulture), remaining.EntityId);
    }
}
