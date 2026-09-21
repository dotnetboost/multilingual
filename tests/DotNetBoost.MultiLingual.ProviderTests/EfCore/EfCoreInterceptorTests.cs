using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>
/// The EF Core provider end to end: application code touches only its own entities, and the
/// translations follow.
/// </summary>
public sealed class EfCoreInterceptorTests
{
    private static Dictionary<string, string?> Name(string value) => new() { ["Name"] = value };

    /// <summary>Product "LAPTOP" with en + ar names, in category "books" with an English name only.</summary>
    private static async Task<int> SeedAsync(Shop shop)
    {
        await using var scope = shop.Scope();

        var category = new ShopCategory { Slug = "books" };
        scope.Translations.Stage(category, "en", Name("Books"));

        var product = new ShopProduct { Sku = "LAPTOP", Price = 10, Category = category };
        scope.Translations.Stage(product, "en", new Dictionary<string, string?> { ["Name"] = "Laptop", ["Description"] = "Light" });
        scope.Translations.Stage(product, "ar", Name("حاسوب"));

        scope.Db.Products.Add(product);
        await scope.Db.SaveChangesAsync();
        return product.Id;
    }

    [Fact]
    public async Task Model_HasNoColumnsForTranslatableProperties()
    {
        await using var shop  = await Shop.StartAsync();
        await using var scope = shop.Scope();
        var model = scope.Db.Model;

        Assert.Null(model.FindEntityType(typeof(ShopProduct))!.FindProperty(nameof(ShopProduct.Name)));
        Assert.Null(model.FindEntityType(typeof(ShopCategory))!.FindProperty(nameof(ShopCategory.Name)));
        Assert.NotNull(model.FindEntityType(typeof(ShopCategory))!.FindProperty(nameof(ShopCategory.Slug)));
    }

    [Fact]
    public async Task Add_WritesTheRequestLanguage_UnderTheGeneratedKey()
    {
        await using var shop  = await Shop.StartAsync();
        await using var scope = await shop.ScopeInAsync("fr");

        var product = new ShopProduct { Sku = "MOUSE", Name = "Souris" };
        scope.Db.Products.Add(product);
        await scope.Db.SaveChangesAsync();

        var row = Assert.Single(await shop.StoredTranslationsAsync());
        Assert.Equal(("ShopProduct", product.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "Name", "fr", "Souris"),
            (row.EntityName, row.EntityId, row.PropertyName, row.LanguageCode, row.Value));
    }

    [Fact]
    public async Task Read_HydratesWithFallback_IncludingIncludedNavigations()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("ar");

        var product = (await scope.Db.Products.AsNoTracking().Include(p => p.Category).ToLocalizedListAsync(scope.Hydrator)).Single();

        Assert.Equal("حاسوب", product.Name);            // Arabic exists
        Assert.Equal("Light", product.Description);    // English fallback
        Assert.Equal("Books", product.Category!.Name); // reached through Include, hydrated too
    }

    [Fact]
    public async Task Update_AfterAFallbackRead_DoesNotPromoteTheFallback()
    {
        await using var shop = await Shop.StartAsync();
        var id = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var product = await scope.Db.Products.Where(p => p.Id == id).FirstLocalizedOrDefaultAsync(scope.Hydrator);
        Assert.Equal("Laptop", product!.Name);          // English, as a fallback

        product.Price = 20;
        await scope.Db.SaveChangesAsync();

        Assert.DoesNotContain(await shop.StoredTranslationsAsync(), t => t.LanguageCode == "fr");
    }

    [Fact]
    public async Task SettingOnlyATranslatableProperty_OnAnUnchangedEntity_IsWritten()
    {
        await using var shop = await Shop.StartAsync();
        var id = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        // Loaded for update WITHOUT hydration, as a write endpoint does. Name is not mapped, so
        // EF still sees the entry as Unchanged after this assignment.
        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
        product.Name = "Ordinateur";
        Assert.Equal(EntityState.Unchanged, scope.Db.Entry(product).State);

        await scope.Db.SaveChangesAsync();

        var french = Assert.Single(await shop.StoredTranslationsAsync(), t => t.LanguageCode == "fr");
        Assert.Equal("Ordinateur", french.Value);
    }

    [Fact]
    public async Task NullTranslatableProperties_LeaveStoredValuesAlone()
    {
        await using var shop = await Shop.StartAsync();
        var id = await SeedAsync(shop);
        var before = await shop.StoredTranslationsAsync();
        await using var scope = await shop.ScopeInAsync("en");

        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
        product.Price = 99;
        await scope.Db.SaveChangesAsync();

        Assert.Equal(before.Select(t => (t.LanguageCode, t.PropertyName, t.Value)),
            (await shop.StoredTranslationsAsync()).Select(t => (t.LanguageCode, t.PropertyName, t.Value)));
    }

    [Fact]
    public async Task Delete_RemovesTheEntitysTranslations()
    {
        await using var shop = await Shop.StartAsync();
        var id = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("en");

        scope.Db.Products.Remove(await scope.Db.Products.SingleAsync(p => p.Id == id));
        await scope.Db.SaveChangesAsync();

        var remaining = await shop.StoredTranslationsAsync();
        Assert.All(remaining, t => Assert.Equal("ShopCategory", t.EntityName));
    }

    [Fact]
    public async Task FailedTranslationWrite_RollsBackTheEntity()
    {
        await using var shop = await Shop.StartAsync();
        await using (var scope = shop.Scope())
        {
            var product = new ShopProduct { Sku = "PEN" };
            scope.Translations.Stage(product, "de", Name("Stift"));   // no such language
            scope.Db.Products.Add(product);

            await Assert.ThrowsAsync<LanguageNotFoundException>(() => scope.Db.SaveChangesAsync());
        }

        await using var check = shop.Scope();
        Assert.False(await check.Db.Products.AnyAsync());
    }

    [Fact]
    public async Task CallersOwnTransaction_IsJoined_AndItsRollbackDiscardsBoth()
    {
        await using var shop = await Shop.StartAsync();
        await using (var scope = await shop.ScopeInAsync("en"))
        {
            await using var transaction = await scope.Db.Database.BeginTransactionAsync();
            scope.Db.Products.Add(new ShopProduct { Sku = "CUP", Name = "Cup" });
            await scope.Db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using var check = shop.Scope();
        Assert.False(await check.Db.Products.AnyAsync());
        Assert.Empty(await shop.StoredTranslationsAsync());
    }

    [Fact]
    public async Task GuidKeys_RoundTrip()
    {
        await using var shop = await Shop.StartAsync();
        var id = Guid.NewGuid();

        await using (var write = await shop.ScopeInAsync("ar"))
        {
            write.Db.Documents.Add(new ShopDocument { Id = id, Title = "دليل" });
            await write.Db.SaveChangesAsync();
        }

        // ShopDocument fixes its entity name, so the rows do not depend on the class name.
        Assert.Equal(("document", id.ToString("D")), (await shop.StoredTranslationsAsync()).Select(t => (t.EntityName, t.EntityId)).Single());

        await using var read = await shop.ScopeInAsync("ar");
        var document = await read.Db.Documents.AsNoTracking().FirstLocalizedOrDefaultAsync(read.Hydrator);

        Assert.Equal("دليل", document!.Title);
    }

    [Fact]
    public async Task SyncSaveChanges_IsSupported()
    {
        await using var shop  = await Shop.StartAsync();
        await using var scope = await shop.ScopeInAsync("en");

        scope.Db.Products.Add(new ShopProduct { Sku = "BAG", Name = "Bag" });
        scope.Db.SaveChanges();

        Assert.Single(await shop.StoredTranslationsAsync());
    }

    [Fact]
    public async Task ManyScopes_ShareOneEfInternalServiceProvider()
    {
        // Regression: a materialization interceptor created per scope becomes part of EF's
        // internal service-provider cache key, and EF throws after twenty distinct providers.
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);

        for (var i = 0; i < 30; i++)
        {
            await using var scope = await shop.ScopeInAsync(i % 2 == 0 ? "en" : "ar");
            var products = await scope.Db.Products.AsNoTracking().ToLocalizedListAsync(scope.Hydrator);
            Assert.NotNull(products.Single().Name);
        }
    }

    [Fact]
    public async Task ExplicitManagerApi_WorksThroughTheEfStores()
    {
        // The API Dapper and MongoDB users will call — exercised here over the EF stores.
        await using var shop = await Shop.StartAsync();
        var id = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var product = new ShopProduct { Id = id };
        await scope.Translations.HydrateAsync([product]);
        Assert.Equal("Laptop", product.Name);

        var all = await scope.Translations.GetAllAsync(product);
        Assert.Equal(["ar", "en"], all.Keys.Order());
    }
}
