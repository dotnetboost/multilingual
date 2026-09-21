using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

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
        var categories = await scope.Db.Categories.AsNoTracking().ToLocalizedListAsync(scope.Hydrator);
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
}
