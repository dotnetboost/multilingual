using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>Filtering and sorting on translated text, run by the database.</summary>
public sealed class TranslatedQueryTests
{
    private static Dictionary<string, string?> Name(string value) => new() { ["Name"] = value };

    /// <summary>LAPTOP: en Laptop, fr Portable · MOUSE: en Mouse only · KEYBOARD: en Keyboard, fr Clavier · BARE: nothing.</summary>
    private static async Task SeedAsync(Shop shop)
    {
        await using var scope = shop.Scope();

        void Add(string sku, string? en, string? fr)
        {
            var product = new ShopProduct { Sku = sku };
            if (en is not null) scope.Translations.Stage(product, "en", Name(en));
            if (fr is not null) scope.Translations.Stage(product, "fr", Name(fr));
            scope.Db.Products.Add(product);
        }

        Add("LAPTOP", "Laptop", "Portable");
        Add("MOUSE", "Mouse", null);
        Add("KEYBOARD", "Keyboard", "Clavier");
        Add("BARE", null, null);
        await scope.Db.SaveChangesAsync();
    }

    private static Task<List<string>> SkusAsync(IQueryable<ShopProduct> query) =>
        query.Select(p => p.Sku).ToListAsync();

    [Fact]
    public async Task Where_ComparesTheRequestedLanguage()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var skus = await SkusAsync(scope.Db.Products.WhereTranslation(scope.Db, p => p.Name, n => n!.StartsWith("Po")));

        Assert.Equal(["LAPTOP"], skus);
    }

    [Fact]
    public async Task Where_FallsBackToTheDefaultLanguage_LikeAReadDoes()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        // MOUSE has no French name, so a read shows the English one — and a filter matches it.
        Assert.Equal(["MOUSE"], await SkusAsync(scope.Db.Products.WhereTranslation(scope.Db, p => p.Name, n => n == "Mouse")));

        // LAPTOP does have a French name, so its English one is not what a read shows.
        Assert.Empty(await SkusAsync(scope.Db.Products.WhereTranslation(scope.Db, p => p.Name, n => n == "Laptop")));
    }

    [Fact]
    public async Task Where_NothingTranslated_IsNull()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        Assert.Equal(["BARE"], await SkusAsync(scope.Db.Products.WhereTranslation(scope.Db, p => p.Name, n => n == null)));
    }

    [Fact]
    public async Task Where_WithAnExplicitLanguage_IgnoresTheCurrentOne()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var skus = await SkusAsync(scope.Db.Products.WhereTranslation(scope.Db, p => p.Name, n => n == "Laptop", languageCode: "EN"));

        Assert.Equal(["LAPTOP"], skus);
    }

    [Fact]
    public async Task Where_WithNoLanguageResolved_UsesTheDefault()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = shop.Scope();

        Assert.Equal(["LAPTOP"], await SkusAsync(scope.Db.Products.WhereTranslation(scope.Db, p => p.Name, n => n == "Laptop")));
    }

    [Fact]
    public async Task OrderBy_SortsByWhatAReadShows()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var withNames = scope.Db.Products.WhereTranslation(scope.Db, p => p.Name, n => n != null);

        // Clavier, Mouse (English fallback), Portable.
        Assert.Equal(["KEYBOARD", "MOUSE", "LAPTOP"], await SkusAsync(withNames.OrderByTranslation(scope.Db, p => p.Name)));
        Assert.Equal(["LAPTOP", "MOUSE", "KEYBOARD"], await SkusAsync(withNames.OrderByTranslationDescending(scope.Db, p => p.Name)));

        // Keyboard, Laptop, Mouse.
        Assert.Equal(["KEYBOARD", "LAPTOP", "MOUSE"], await SkusAsync(withNames.OrderByTranslation(scope.Db, p => p.Name, "en")));
    }

    [Fact]
    public async Task ThenBy_BreaksTies()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var skus = await SkusAsync(scope.Db.Products
            .OrderBy(p => p.Price)                               // all equal
            .ThenByTranslationDescending(scope.Db, p => p.Name)
            .Where(p => p.Sku != "BARE"));

        Assert.Equal(["LAPTOP", "MOUSE", "KEYBOARD"], skus);
    }

    [Fact]
    public async Task ComposesWithHydration()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var products = await scope.Db.Products.AsNoTracking()
            .WhereTranslation(scope.Db, p => p.Name, n => n!.EndsWith("er") || n.EndsWith("le"))
            .OrderByTranslation(scope.Db, p => p.Name)
            .ToLocalizedListAsync(scope.Hydrator);

        Assert.Equal(["Clavier", "Portable"], products.Select(p => p.Name));
    }

    [Fact]
    public async Task GuidKeys_Match()
    {
        await using var shop = await Shop.StartAsync();
        var id = Guid.NewGuid();
        await using (var write = await shop.ScopeInAsync("ar"))
        {
            write.Db.Documents.Add(new ShopDocument { Id = id, Title = "دليل" });
            write.Db.Documents.Add(new ShopDocument { Id = Guid.NewGuid(), Title = "كتاب" });
            await write.Db.SaveChangesAsync();
        }

        await using var scope = await shop.ScopeInAsync("ar");
        var ids = await scope.Db.Documents.WhereTranslation(scope.Db, d => d.Title, t => t == "دليل").Select(d => d.Id).ToListAsync();

        Assert.Equal([id], ids);
    }

    [Fact]
    public async Task UnknownLanguage_Throws()
    {
        await using var shop  = await Shop.StartAsync();
        await using var scope = shop.Scope();

        Assert.Throws<LanguageNotFoundException>(() =>
            scope.Db.Products.WhereTranslation(scope.Db, p => p.Name, n => n != null, languageCode: "de"));
    }

    [Fact]
    public async Task NonTranslatableProperty_Throws()
    {
        await using var shop  = await Shop.StartAsync();
        await using var scope = shop.Scope();

        var ex = Assert.Throws<ArgumentException>(() => scope.Db.Products.OrderByTranslation(scope.Db, p => p.Sku));
        Assert.Contains("not a translatable property", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================== base-language mode

    [Fact]
    public async Task BaseLanguageMode_FallsBackToTheColumn_AndServesTheBaseLanguageFromIt()
    {
        await using var shop = await BaseShop.StartAsync();
        var laptop = await shop.CreateProductAsync("LAPTOP", "Laptop");
        await shop.CreateProductAsync("MOUSE", "Mouse");

        await using (var write = await shop.ScopeInAsync("ar"))
        {
            (await write.Db.Products.SingleAsync(p => p.Id == laptop)).Name = "حاسوب";
            await write.Db.SaveChangesAsync();
        }

        await using var ar = await shop.ScopeInAsync("ar");
        Assert.Equal(["LAPTOP"], await ar.Db.Products.WhereTranslation(ar.Db, p => p.Name, n => n == "حاسوب").Select(p => p.Sku).ToListAsync());
        Assert.Equal(["MOUSE"], await ar.Db.Products.WhereTranslation(ar.Db, p => p.Name, n => n == "Mouse").Select(p => p.Sku).ToListAsync());

        // "Mouse" (column) sorts before "حاسوب".
        Assert.Equal(["MOUSE", "LAPTOP"], await ar.Db.Products.OrderByTranslation(ar.Db, p => p.Name).Select(p => p.Sku).ToListAsync());

        await using var en = await shop.ScopeInAsync("en");
        Assert.Equal(["LAPTOP"], await en.Db.Products.WhereTranslation(en.Db, p => p.Name, n => n == "Laptop").Select(p => p.Sku).ToListAsync());
    }
}
