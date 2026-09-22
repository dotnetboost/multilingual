using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>
/// What happens around the edges of the read and write paths: hydration that must not clobber a
/// caller's edits, a failed save that must leave the change tracker retryable, and a registry
/// that must not fail silently when it fills up.
/// </summary>
public sealed class EfCoreRecoveryTests
{
    private static Dictionary<string, string?> Name(string value) => new() { ["Name"] = value };

    private static async Task<int> SeedProductAsync(Shop shop)
    {
        await using var scope = shop.Scope();

        var product = new ShopProduct { Sku = "LAPTOP", Price = 10 };
        scope.Translations.Stage(product, "en", Name("Laptop"));
        scope.Translations.Stage(product, "fr", Name("Portable"));
        scope.Db.Products.Add(product);

        var category = new ShopCategory { Slug = "books" };
        scope.Translations.Stage(category, "en", Name("Books"));
        scope.Db.Categories.Add(category);

        await scope.Db.SaveChangesAsync();
        return product.Id;
    }

    /// <summary>Creates a language from its own scope, so nothing else on the test's context is saved with it.</summary>
    private static async Task CreateLanguageAsync(Shop shop, string code)
    {
        await using var admin = shop.Scope();
        await admin.Get<ILanguageManager>().CreateAsync(new NewLanguage { Code = code, Name = code });
    }

    private static async Task CreateLanguageAsync(BaseShop shop, string code)
    {
        await using var admin = shop.Scope();
        await admin.Get<ILanguageManager>().CreateAsync(new NewLanguage { Code = code, Name = code });
    }

    // ================================================================== hydration vs. edits

    [Fact]
    public async Task LaterLocalizedQuery_DoesNotOverwrite_AValueAssignedAfterLoading()
    {
        await using var shop = await Shop.StartAsync();
        var id = await SeedProductAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        // Loaded for update, not hydrated — so it stays queued in the registry.
        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
        product.Name = "Ordinateur";

        // Some unrelated read in the same scope drains the registry.
        var categories = await scope.Db.Categories.AsNoTracking().ToLocalizedListAsync();
        Assert.Equal("Books", Assert.Single(categories).Name);

        Assert.Equal("Ordinateur", product.Name);
        await scope.Db.SaveChangesAsync();

        var french = Assert.Single(await shop.StoredTranslationsAsync(), t => t is { EntityName: "ShopProduct", LanguageCode: "fr" });
        Assert.Equal("Ordinateur", french.Value);
    }

    [Fact]
    public async Task UntouchedEntities_LoadedEarlier_AreStillHydrated()
    {
        await using var shop = await Shop.StartAsync();
        var id = await SeedProductAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var product = await scope.Db.Products.AsNoTracking().SingleAsync(p => p.Id == id);
        await scope.Hydrator.HydratePendingAsync();

        Assert.Equal("Portable", product.Name);
    }

    // ================================================================== failed save, then retry

    [Fact]
    public async Task FailedTranslationWrite_LeavesAnAddedEntityAdded_SoARetrySavesIt()
    {
        await using var shop = await Shop.StartAsync();
        await using var scope = await shop.ScopeInAsync("en");

        var product = new ShopProduct { Sku = "PEN", Name = "Pen" };
        scope.Translations.Stage(product, "de", Name("Stift"));   // no such language yet
        scope.Db.Products.Add(product);

        await Assert.ThrowsAsync<UnknownLanguageException>(() => scope.Db.SaveChangesAsync());

        var entry = scope.Db.Entry(product);
        Assert.Equal(EntityState.Added, entry.State);
        Assert.True(entry.Property(p => p.Id).IsTemporary);

        await CreateLanguageAsync(shop, "de");
        await scope.Db.SaveChangesAsync();

        await using var check = shop.Scope();
        var stored = await check.Db.Products.AsNoTracking().SingleAsync();
        Assert.Equal(stored.Id, product.Id);

        var rows = await shop.StoredTranslationsAsync();
        Assert.All(rows, t => Assert.Equal(product.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), t.EntityId));
        Assert.Equal(["de", "en"], rows.Select(t => t.LanguageCode).Order());
    }

    [Fact]
    public async Task FailedTranslationWrite_KeepsAnAddedGraphLinked_SoARetrySavesBoth()
    {
        await using var shop = await Shop.StartAsync();
        await using var scope = await shop.ScopeInAsync("en");

        var category = new ShopCategory { Slug = "pens", Name = "Pens" };
        var product  = new ShopProduct { Sku = "PEN", Name = "Pen", Category = category };
        scope.Translations.Stage(product, "de", Name("Stift"));
        scope.Db.Products.Add(product);

        await Assert.ThrowsAsync<UnknownLanguageException>(() => scope.Db.SaveChangesAsync());

        Assert.Equal(EntityState.Added, scope.Db.Entry(category).State);
        Assert.Same(category, product.Category);
        Assert.Equal(category.Id, product.CategoryId);
        Assert.True(scope.Db.Entry(product).Property(p => p.CategoryId).IsTemporary);

        await CreateLanguageAsync(shop, "de");
        await scope.Db.SaveChangesAsync();

        await using var check = shop.Scope();
        var stored = await check.Db.Products.AsNoTracking().Include(p => p.Category).SingleAsync();
        Assert.Equal("pens", stored.Category!.Slug);
        Assert.Equal(category.Id, stored.CategoryId);
    }

    [Fact]
    public async Task FailedTranslationWrite_WithSyncSaveChanges_IsAlsoRetryable()
    {
        await using var shop = await Shop.StartAsync();
        await using var scope = await shop.ScopeInAsync("en");

        var product = new ShopProduct { Sku = "PEN", Name = "Pen" };
        scope.Translations.Stage(product, "de", Name("Stift"));
        scope.Db.Products.Add(product);

        Assert.Throws<UnknownLanguageException>(() => scope.Db.SaveChanges());
        Assert.Equal(EntityState.Added, scope.Db.Entry(product).State);

        await CreateLanguageAsync(shop, "de");
        scope.Db.SaveChanges();

        Assert.Equal(2, (await shop.StoredTranslationsAsync()).Count);
    }

    [Fact]
    public async Task FailedTranslationWrite_LeavesAModifiedEntityModified_SoARetrySavesIt()
    {
        await using var shop = await Shop.StartAsync();
        var id = await SeedProductAsync(shop);
        await using var scope = await shop.ScopeInAsync("en");

        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
        product.Price = 42;
        scope.Translations.Stage(product, "de", Name("Laptop (de)"));

        await Assert.ThrowsAsync<UnknownLanguageException>(() => scope.Db.SaveChangesAsync());

        var entry = scope.Db.Entry(product);
        Assert.Equal(EntityState.Modified, entry.State);
        Assert.True(entry.Property(p => p.Price).IsModified);
        Assert.False(entry.Property(p => p.Sku).IsModified);
        Assert.Equal(10m, entry.Property(p => p.Price).OriginalValue);

        await CreateLanguageAsync(shop, "de");
        await scope.Db.SaveChangesAsync();

        await using var check = shop.Scope();
        Assert.Equal(42m, (await check.Db.Products.AsNoTracking().SingleAsync(p => p.Id == id)).Price);
        Assert.Contains(await shop.StoredTranslationsAsync(), t => t.LanguageCode == "de");
    }

    [Fact]
    public async Task FailedTranslationWrite_LeavesADeletedEntityDeleted_SoARetryDeletesIt()
    {
        await using var shop = await Shop.StartAsync();
        var id = await SeedProductAsync(shop);
        await using var scope = await shop.ScopeInAsync("en");

        var doomed = await scope.Db.Products.SingleAsync(p => p.Id == id);
        scope.Db.Products.Remove(doomed);

        var category = await scope.Db.Categories.SingleAsync();
        scope.Translations.Stage(category, "de", Name("Bücher"));

        await Assert.ThrowsAsync<UnknownLanguageException>(() => scope.Db.SaveChangesAsync());
        Assert.Equal(EntityState.Deleted, scope.Db.Entry(doomed).State);

        await CreateLanguageAsync(shop, "de");
        await scope.Db.SaveChangesAsync();

        await using var check = shop.Scope();
        Assert.False(await check.Db.Products.AnyAsync());
        Assert.DoesNotContain(await shop.StoredTranslationsAsync(), t => t.EntityName == "ShopProduct");
    }

    [Fact]
    public async Task FailedTranslationWrite_InBaseLanguageMode_KeepsTheAuthoredValue_ForTheRetry()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");
        await using var scope = await shop.ScopeInAsync("ar");

        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
        product.Name = "حاسوب";
        scope.Translations.Stage(product, "de", Name("Laptop (de)"));

        await Assert.ThrowsAsync<UnknownLanguageException>(() => scope.Db.SaveChangesAsync());
        Assert.True(scope.Db.Entry(product).Property(p => p.Name).IsModified);

        await CreateLanguageAsync(shop, "de");
        await scope.Db.SaveChangesAsync();

        Assert.Equal("Laptop", await shop.StoredColumnNameAsync(id));
        var arabic = Assert.Single(await shop.StoredTranslationsAsync(), t => t.LanguageCode == "ar");
        Assert.Equal("حاسوب", arabic.Value);
    }

    // ================================================================== registry capacity

    [Fact]
    public void Registry_PastCapacity_RefusesToDrainAPartialSet()
    {
        var registry = new MaterializedEntityRegistry(NullLogger<MaterializedEntityRegistry>.Instance);

        for (var i = 0; i <= MaterializedEntityRegistry.Capacity; i++)
            registry.Register(new ShopCategory { Id = i });

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Drain());
        Assert.Contains(MaterializedEntityRegistry.Capacity.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);

        // The scope can carry on: the overflow is reported once, and the registry starts empty.
        Assert.Empty(registry.Drain());
        registry.Register(new ShopCategory { Id = -1 });
        Assert.Single(registry.Drain());
    }

    [Fact]
    public void Registry_AtCapacity_DrainsEverything()
    {
        var registry = new MaterializedEntityRegistry(NullLogger<MaterializedEntityRegistry>.Instance);

        for (var i = 0; i < MaterializedEntityRegistry.Capacity; i++)
            registry.Register(new ShopCategory { Id = i });

        Assert.Equal(MaterializedEntityRegistry.Capacity, registry.Drain().Count);
    }
}
