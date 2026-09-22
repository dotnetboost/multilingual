using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>
/// Every EF Core query returns translated entities, with nothing but EF Core's own methods: the
/// shapes a query can take, the ways EF loads entities, and the navigations it includes.
/// </summary>
public sealed class AutomaticTranslationTests
{
    private static Dictionary<string, string?> Name(string value) => new() { ["Name"] = value };

    /// <summary>
    /// Category "books" (en Books, fr Livres) holding LAPTOP (en Laptop, fr Portable) and MOUSE
    /// (en Mouse only); category "toys" (en Toys) holding nothing.
    /// </summary>
    private static async Task<(int Books, int Laptop)> SeedAsync(Shop shop)
    {
        await using var scope = shop.Scope();

        var books = new ShopCategory { Slug = "books" };
        scope.Translations.Stage(books, "en", Name("Books"));
        scope.Translations.Stage(books, "fr", Name("Livres"));

        var toys = new ShopCategory { Slug = "toys" };
        scope.Translations.Stage(toys, "en", Name("Toys"));

        var laptop = new ShopProduct { Sku = "LAPTOP", Price = 10, Category = books };
        scope.Translations.Stage(laptop, "en", Name("Laptop"));
        scope.Translations.Stage(laptop, "fr", Name("Portable"));

        var mouse = new ShopProduct { Sku = "MOUSE", Price = 5, Category = books };
        scope.Translations.Stage(mouse, "en", Name("Mouse"));

        scope.Db.AddRange(laptop, mouse, toys);
        await scope.Db.SaveChangesAsync();
        return (books.Id, laptop.Id);
    }

    // ================================================================== result shapes

    [Fact]
    public async Task EntitiesInsideAProjection_AreFilled()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var rows = await scope.Db.Products.AsNoTracking()
            .OrderBy(p => p.Sku)
            .Select(p => new { Product = p, p.Category, p.Price })
            .ToListAsync();

        Assert.Equal(["Portable", "Mouse"], rows.Select(r => r.Product.Name));   // MOUSE: English fallback
        Assert.All(rows, r => Assert.Equal("Livres", r.Category!.Name));
    }

    [Fact]
    public async Task ANavigationProjectedOnItsOwn_IsFilled()
    {
        await using var shop = await Shop.StartAsync();
        var (_, laptop) = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var category = await scope.Db.Products.AsNoTracking().Where(p => p.Id == laptop).Select(p => p.Category).SingleAsync();

        Assert.Equal("Livres", category!.Name);
    }

    [Fact]
    public async Task SynchronousAndStreamedQueries_AreFilled()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        Assert.Equal(["Portable", "Mouse"], scope.Db.Products.AsNoTracking().OrderBy(p => p.Sku).ToList().Select(p => p.Name));

        var streamed = new List<string?>();
        await foreach (var product in scope.Db.Products.AsNoTracking().OrderBy(p => p.Sku).AsAsyncEnumerable())
            streamed.Add(product.Name);

        Assert.Equal(["Portable", "Mouse"], streamed);
    }

    [Fact]
    public async Task AggregatesAndExistence_CompareTheTranslatedValue()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        Assert.Equal(1, await scope.Db.Products.CountAsync(p => p.Name == "Portable"));
        Assert.False(await scope.Db.Products.AnyAsync(p => p.Name == "Laptop"));   // French reads "Portable"
        Assert.True(await scope.Db.Products.AnyAsync(p => p.Name == "Mouse"));     // fallback
    }

    // ================================================================== how EF loads entities

    [Fact]
    public async Task Find_IsFilled()
    {
        await using var shop = await Shop.StartAsync();
        var (_, laptop) = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        Assert.Equal("Portable", (await scope.Db.Products.FindAsync(laptop))!.Name);
    }

    [Fact]
    public async Task ExplicitLoading_FillsTheLoadedEntities()
    {
        await using var shop = await Shop.StartAsync();
        var (books, _) = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var category = await scope.Db.Categories.SingleAsync(c => c.Id == books);
        await scope.Db.Entry(category).Collection(c => c.Products).LoadAsync();

        Assert.Equal(["Mouse", "Portable"], category.Products.Select(p => p.Name).Order());
    }

    // ================================================================== includes

    [Fact]
    public async Task ACollectionInclude_FillsEveryChild()
    {
        await using var shop = await Shop.StartAsync();
        var (books, _) = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var category = await scope.Db.Categories.AsNoTracking().Include(c => c.Products).SingleAsync(c => c.Id == books);

        Assert.Equal("Livres", category.Name);
        Assert.Equal(["Mouse", "Portable"], category.Products.Select(p => p.Name).Order());
    }

    [Fact]
    public async Task ACollectionInclude_AsASplitQuery_FillsEveryChild()
    {
        await using var shop = await Shop.StartAsync();
        var (books, _) = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var category = await scope.Db.Categories.AsNoTracking().Include(c => c.Products).AsSplitQuery().SingleAsync(c => c.Id == books);

        Assert.Equal(["Mouse", "Portable"], category.Products.Select(p => p.Name).Order());
    }

    [Fact]
    public async Task AFilteredInclude_FillsTheChildrenItLoads()
    {
        await using var shop = await Shop.StartAsync();
        var (books, _) = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var category = await scope.Db.Categories.AsNoTracking()
            .Include(c => c.Products.Where(p => p.Name == "Portable"))
            .SingleAsync(c => c.Id == books);

        Assert.Equal("Portable", Assert.Single(category.Products).Name);
    }

    [Fact]
    public async Task ThenInclude_FillsEveryLevel()
    {
        await using var shop = await Shop.StartAsync();
        var (_, laptop) = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        // Tracking: Product → Category → Products leads back to Product, a cycle EF only allows
        // in tracking queries.
        var product = await scope.Db.Products
            .Include(p => p.Category).ThenInclude(c => c!.Products)
            .SingleAsync(p => p.Id == laptop);

        Assert.Equal("Portable", product.Name);
        Assert.Equal("Livres", product.Category!.Name);
        Assert.Equal(["Mouse", "Portable"], product.Category.Products.Select(p => p.Name).Order());
    }

    [Fact]
    public async Task AStringInclude_IsFilledToo()
    {
        await using var shop = await Shop.StartAsync();
        var (_, laptop) = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var product = await scope.Db.Products.Include("Category.Products").SingleAsync(p => p.Id == laptop);

        Assert.Equal("Livres", product.Category!.Name);
        Assert.Equal(["Mouse", "Portable"], product.Category.Products.Select(p => p.Name).Order());
    }

    // ================================================================== bulk operations

    [Fact]
    public async Task ExecuteDelete_MatchesTheTranslatedValue()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var deleted = await scope.Db.Products.Where(p => p.Name == "Portable").ExecuteDeleteAsync();

        Assert.Equal(1, deleted);
        Assert.Equal(["MOUSE"], await scope.Db.Products.Select(p => p.Sku).ToListAsync());
    }

    // ================================================================== per-query switches

    [Fact]
    public async Task InLanguage_ReadsEverythingInThatLanguage_WhateverTheRequest()
    {
        await using var shop = await Shop.StartAsync();
        var (_, laptop) = await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var product = await scope.Db.Products.AsNoTracking().InLanguage("en").Include(p => p.Category).SingleAsync(p => p.Id == laptop);

        Assert.Equal("Laptop", product.Name);
        Assert.Equal("Books", product.Category!.Name);
    }

    [Fact]
    public async Task InLanguage_AndTheRequestLanguage_ShareNothing_AcrossExecutions()
    {
        await using var shop = await Shop.StartAsync();
        var (_, laptop) = await SeedAsync(shop);

        async Task<string?> NameAsync(string requestLanguage, string? queryLanguage)
        {
            await using var scope = await shop.ScopeInAsync(requestLanguage);
            var query = scope.Db.Products.AsNoTracking().Where(p => p.Id == laptop);
            return (await (queryLanguage is null ? query : query.InLanguage(queryLanguage)).SingleAsync()).Name;
        }

        Assert.Equal("Portable", await NameAsync("fr", null));
        Assert.Equal("Laptop", await NameAsync("fr", "en"));
        Assert.Equal("Laptop", await NameAsync("en", null));
        Assert.Equal("Portable", await NameAsync("en", "fr"));
    }

    [Fact]
    public async Task ACategoryWithoutTheRequestedLanguage_FallsBack_AndOneWithNothing_IsNull()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var names = await scope.Db.Categories.AsNoTracking().OrderBy(c => c.Slug).Select(c => c.Name).ToListAsync();

        Assert.Equal(["Livres", "Toys"], names);   // toys: no French, so the English default
    }
}
