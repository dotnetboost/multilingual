using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>
/// The application-facing surface on EF Core: <c>IMultiLingualManager</c> plus the localized
/// query extensions, which find the context's services on their own.
/// </summary>
public sealed class MultiLingualManagerEfCoreTests
{
    /// <summary>Product "LAPTOP": Name in en, ar and fr.</summary>
    private static async Task<int> SeedAsync(Shop shop)
    {
        await using var scope = await shop.ScopeInAsync("en");

        var product = new ShopProduct { Sku = "LAPTOP", Name = "Laptop" };
        scope.MultiLingual.For(product)
            .Set("ar", p => p.Name, "حاسوب")
            .Set("fr", p => p.Name, "Ordinateur");

        scope.Db.Products.Add(product);
        await scope.Db.SaveChangesAsync();
        return product.Id;
    }

    [Fact]
    public async Task Clear_ThenSaveChanges_RemovesTheLanguage()
    {
        await using var shop = await Shop.StartAsync();
        var id = await SeedAsync(shop);

        await using (var scope = await shop.ScopeInAsync("en"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            Assert.Equal(["ar", "en", "fr"], (await scope.MultiLingual.For(product).GetAllAsync()).Keys.Order());

            scope.MultiLingual.For(product).Clear("ar");
            await scope.Db.SaveChangesAsync();
        }

        Assert.DoesNotContain(await shop.StoredTranslationsAsync(), t => t.LanguageCode == "ar");

        await using var read = await shop.ScopeInAsync("ar");
        var localized = await read.Db.Products.AsNoTracking().Where(p => p.Id == id).FirstLocalizedOrDefaultAsync();
        Assert.Equal("Laptop", localized!.Name);   // falls back to English now
    }

    [Fact]
    public async Task Clear_AnUnknownLanguage_FailsBeforeSaveChanges()
    {
        await using var shop = await Shop.StartAsync();
        var id = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("en");

        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);

        Assert.Throws<LanguageNotFoundException>(() => scope.MultiLingual.For(product).Clear("de"));
    }

    [Fact]
    public async Task LocalizedQuery_ThatFindsNothing_ReturnsEmpty()
    {
        await using var shop  = await Shop.StartAsync();
        await using var scope = await shop.ScopeInAsync("ar");

        Assert.Empty(await scope.Db.Products.ToLocalizedListAsync());
        Assert.Null(await scope.Db.Products.FirstLocalizedOrDefaultAsync());
        Assert.Null(scope.Db.Products.SingleLocalizedOrDefault());
    }

    [Fact]
    public async Task LocalizedQuery_OnAPlainEntityType_IsJustTheQuery()
    {
        await using var shop  = await Shop.StartAsync();
        await using var scope = await shop.ScopeInAsync("ar");

        var languages = await scope.Db.Languages.AsNoTracking().OrderBy(l => l.Code).ToLocalizedListAsync();

        Assert.Equal(["ar", "en", "fr"], languages.Select(l => l.Code));
    }

    [Fact]
    public async Task ConcurrentLocalizedReads_OnDifferentContexts_EachLocalizeTheirOwn()
    {
        // Two applications (each its own database connection), read at the same time in different
        // languages. Each read has to find its own context's services — never the other one's.
        await using var arabicShop = await Shop.StartAsync();
        await using var frenchShop = await Shop.StartAsync();
        await SeedAsync(arabicShop);
        await SeedAsync(frenchShop);

        async Task<IReadOnlyList<string?>> ReadManyAsync(Shop shop, string language)
        {
            var names = new List<string?>();
            for (var i = 0; i < 25; i++)
            {
                await using var scope = await shop.ScopeInAsync(language);
                await Task.Yield();
                names.AddRange((await scope.Db.Products.AsNoTracking().ToLocalizedListAsync()).Select(p => p.Name));
            }

            return names;
        }

        var results = await Task.WhenAll(
            Task.Run(() => ReadManyAsync(arabicShop, "ar")),
            Task.Run(() => ReadManyAsync(frenchShop, "fr")));

        Assert.All(results[0], name => Assert.Equal("حاسوب", name));
        Assert.All(results[1], name => Assert.Equal("Ordinateur", name));
    }
}
