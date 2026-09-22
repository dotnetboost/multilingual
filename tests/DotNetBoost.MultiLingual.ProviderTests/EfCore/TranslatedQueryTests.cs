using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>Plain EF Core LINQ over translated text — filters, sorting, projections, entities — run by the database.</summary>
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

    // ================================================================== filtering

    [Fact]
    public async Task Where_ComparesTheRequestedLanguage()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var skus = await SkusAsync(scope.Db.Products.Where(p => p.Name!.StartsWith("Po")));

        Assert.Equal(["LAPTOP"], skus);
    }

    [Fact]
    public async Task Where_FallsBackToTheDefaultLanguage_LikeAReadDoes()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        // MOUSE has no French name, so a read shows the English one — and a filter matches it.
        Assert.Equal(["MOUSE"], await SkusAsync(scope.Db.Products.Where(p => p.Name == "Mouse")));

        // LAPTOP does have a French name, so its English one is not what a read shows.
        Assert.Empty(await SkusAsync(scope.Db.Products.Where(p => p.Name == "Laptop")));
    }

    [Fact]
    public async Task Where_NothingTranslated_IsNull()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        Assert.Equal(["BARE"], await SkusAsync(scope.Db.Products.Where(p => p.Name == null)));
    }

    [Fact]
    public async Task Where_WithAnExplicitLanguage_IgnoresTheCurrentOne()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var skus = await SkusAsync(scope.Db.Products.InLanguage("EN").Where(p => p.Name == "Laptop"));

        Assert.Equal(["LAPTOP"], skus);
    }

    [Fact]
    public async Task Where_WithNoLanguageResolved_UsesTheDefault()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = shop.Scope();

        Assert.Equal(["LAPTOP"], await SkusAsync(scope.Db.Products.Where(p => p.Name == "Laptop")));
    }

    [Fact]
    public async Task OrdinaryFilters_WorkBeforeAndAfter_AndAreNotTranslated()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var before = await SkusAsync(scope.Db.Products
            .Where(p => p.Sku != "KEYBOARD")
            
            .Where(p => p.Name!.StartsWith("Po") || p.Name.StartsWith("Cl")));

        var after = await SkusAsync(scope.Db.Products
            
            .Where(p => p.Name!.StartsWith("Po") || p.Name.StartsWith("Cl"))
            .Where(p => p.Sku != "KEYBOARD"));

        // Portable and Clavier match the translated filter; KEYBOARD is excluded by the ordinary one.
        Assert.Equal(["LAPTOP"], before);
        Assert.Equal(before, after);
    }

    // ================================================================== sorting

    [Fact]
    public async Task OrderBy_SortsByWhatAReadShows()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        IQueryable<ShopProduct> WithNames(string? language = null) =>
            (language is null ? scope.Db.Products : scope.Db.Products.InLanguage(language)).Where(p => p.Name != null);

        // Clavier, Mouse (English fallback), Portable.
        Assert.Equal(["KEYBOARD", "MOUSE", "LAPTOP"], await SkusAsync(WithNames().OrderBy(p => p.Name)));
        Assert.Equal(["LAPTOP", "MOUSE", "KEYBOARD"], await SkusAsync(WithNames().OrderByDescending(p => p.Name)));

        // Keyboard, Laptop, Mouse.
        Assert.Equal(["KEYBOARD", "LAPTOP", "MOUSE"], await SkusAsync(WithNames("en").OrderBy(p => p.Name)));
    }

    [Fact]
    public async Task ThenBy_BreaksTies()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var skus = await SkusAsync(scope.Db.Products
            
            .OrderBy(p => p.Price)                               // all equal
            .ThenByDescending(p => p.Name)
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
            
            .Where(p => p.Name!.EndsWith("er") || p.Name.EndsWith("le"))
            .OrderBy(p => p.Name)
            .ToListAsync();

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
        var ids = await scope.Db.Documents.Where(d => d.Title == "دليل").Select(d => d.Id).ToListAsync();

        Assert.Equal([id], ids);
    }

    // ================================================================== one compiled query, every language

    [Fact]
    public async Task TheSameQuery_RunInDifferentLanguages_AnswersEachInItsOwn()
    {
        // EF compiles a query shape once and reuses it. The language must therefore travel as a
        // parameter; were it baked into the compiled query, the second language would get the first's.
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);

        async Task<List<string?>> NamesAsync(string language)
        {
            await using var scope = await shop.ScopeInAsync(language);
            return await scope.Db.Products
                .Where(p => p.Sku == "LAPTOP" || p.Sku == "KEYBOARD")
                .OrderBy(p => p.Sku)
                .Select(p => p.Name)
                .ToListAsync();
        }

        Assert.Equal(["Clavier", "Portable"], await NamesAsync("fr"));
        Assert.Equal(["Keyboard", "Laptop"], await NamesAsync("ar"));   // no Arabic: English fallback
        Assert.Equal(["Clavier", "Portable"], await NamesAsync("fr"));
    }

    [Fact]
    public async Task CompiledSql_CarriesLanguageTokens_AndLiteralNames_NotParameters()
    {
        await using var shop = await Shop.StartAsync();
        await using var scope = await shop.ScopeInAsync("fr");

        // ToQueryString shows the SQL as compiled, shared by every language: the languages are
        // placeholders, filled in per execution — so no language parameter, and no bloat.
        var sql = scope.Db.Products
            .Select(p => new { p.Name, p.Description, Category = p.Category!.Name })
            .ToQueryString();

        Assert.DoesNotContain(".param set", sql, StringComparison.Ordinal);
        Assert.Contains("'~ml:L~'", sql, StringComparison.Ordinal);
        Assert.Contains("'~ml:F~'", sql, StringComparison.Ordinal);
        Assert.Contains("'ShopProduct'", sql, StringComparison.Ordinal);
        Assert.Contains("'Description'", sql, StringComparison.Ordinal);
    }

    // ================================================================== misuse

    [Fact]
    public async Task InLanguage_WithAnUnknownCode_ThrowsWhenTheQueryRuns()
    {
        await using var shop  = await Shop.StartAsync();
        await using var scope = shop.Scope();

        var query = scope.Db.Products.InLanguage("de");   // nothing checked yet: no context at hand

        await Assert.ThrowsAsync<UnknownLanguageException>(() => query.ToListAsync());
    }

    [Fact]
    public async Task InLanguage_WithTwoDifferentLanguages_IsRefused()
    {
        await using var shop  = await Shop.StartAsync();
        await using var scope = await shop.ScopeInAsync("fr");

        var query = scope.Db.Products.InLanguage("en").Where(p => p.Price > 0).InLanguage("fr");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync());
        Assert.Contains("more than one language", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoreTranslations_ReturnsEntitiesUnfilled()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var product = await scope.Db.Products.AsNoTracking().IgnoreTranslations().SingleAsync(p => p.Sku == "LAPTOP");

        Assert.Null(product.Name);   // default mode: no column, and nothing filled in
    }

    // ================================================================== projection

    /// <summary>Category "books" (en Books, fr Livres) holding LAPTOP (en Laptop, fr Portable); MOUSE (en Mouse) in none.</summary>
    private static async Task SeedCatalogueAsync(Shop shop)
    {
        await using var scope = shop.Scope();

        var books = new ShopCategory { Slug = "books" };
        scope.Translations.Stage(books, "en", Name("Books"));
        scope.Translations.Stage(books, "fr", Name("Livres"));

        var laptop = new ShopProduct { Sku = "LAPTOP", Category = books };
        scope.Translations.Stage(laptop, "en", Name("Laptop"));
        scope.Translations.Stage(laptop, "fr", Name("Portable"));

        var mouse = new ShopProduct { Sku = "MOUSE" };
        scope.Translations.Stage(mouse, "en", Name("Mouse"));

        scope.Db.Products.AddRange(laptop, mouse);
        await scope.Db.SaveChangesAsync();
    }

    private sealed record ProductRow(string Sku, string? Name, string? Category);

    [Fact]
    public async Task Select_ReadsTheRequestedLanguage_ThroughNavigationsToo()
    {
        await using var shop = await Shop.StartAsync();
        await SeedCatalogueAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var rows = await scope.Db.Products
            
            .OrderBy(p => p.Sku)
            .Select(p => new ProductRow(p.Sku, p.Name, p.Category!.Name))
            .ToListAsync();

        Assert.Equal(
            [new ProductRow("LAPTOP", "Portable", "Livres"), new ProductRow("MOUSE", "Mouse", null)],   // MOUSE: English fallback, no category
            rows);
    }

    [Fact]
    public async Task Select_TranslatesNestedCollectionProjections()
    {
        await using var shop = await Shop.StartAsync();
        await SeedCatalogueAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var category = await scope.Db.Categories
            
            .Select(c => new { c.Name, Products = c.Products.Select(p => p.Name).ToList() })
            .SingleAsync();

        Assert.Equal("Livres", category.Name);
        Assert.Equal(["Portable"], category.Products);
    }

    [Fact]
    public async Task Select_WithAnExplicitLanguage()
    {
        await using var shop = await Shop.StartAsync();
        await SeedCatalogueAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var names = await scope.Db.Products
            .InLanguage("en")
            .Where(p => p.Name != null)
            .OrderByDescending(p => p.Name)
            .Select(p => p.Name)
            .ToListAsync();

        Assert.Equal(["Mouse", "Laptop"], names);
    }

    [Fact]
    public async Task Include_FillsTheNavigationToo()
    {
        await using var shop = await Shop.StartAsync();
        await SeedCatalogueAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var laptop = await scope.Db.Products.AsNoTracking()
            
            .Include(p => p.Category)
            .Where(p => p.Name == "Portable")
            .FirstOrDefaultAsync();

        Assert.Equal("Portable", laptop!.Name);
        Assert.Equal("Livres", laptop.Category!.Name);
    }

    [Fact]
    public async Task IgnoredProjection_OfAColumnlessProperty_IsRefused_InsteadOfReturningNull()
    {
        await using var shop = await Shop.StartAsync();
        await SeedCatalogueAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Db.Products.IgnoreTranslations().Select(p => new { p.Sku, p.Name }).ToListAsync());

        Assert.Contains("ShopProduct.Name", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IgnoreTranslations", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoredFilter_OnAColumnlessProperty_IsRefused_WithTheSameClearMessage()
    {
        await using var shop  = await Shop.StartAsync();
        await using var scope = await shop.ScopeInAsync("fr");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Db.Categories.IgnoreTranslations().Where(c => c.Products.Any(p => p.Name == "Portable")).ToListAsync());

        Assert.Contains("ShopProduct.Name", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IgnoreTranslations", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilterThroughACollection_ComparesTheTranslatedValue()
    {
        await using var shop = await Shop.StartAsync();
        await SeedCatalogueAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var categories = await scope.Db.Categories.Where(c => c.Products.Any(p => p.Name == "Portable")).ToListAsync();

        Assert.Equal("Livres", Assert.Single(categories).Name);
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
        Assert.Equal(["LAPTOP"], await ar.Db.Products.Where(p => p.Name == "حاسوب").Select(p => p.Sku).ToListAsync());
        Assert.Equal(["MOUSE"], await ar.Db.Products.Where(p => p.Name == "Mouse").Select(p => p.Sku).ToListAsync());

        // "Mouse" (column) sorts before "حاسوب".
        Assert.Equal(["MOUSE", "LAPTOP"], await ar.Db.Products.OrderBy(p => p.Name).Select(p => p.Sku).ToListAsync());

        await using var en = await shop.ScopeInAsync("en");
        Assert.Equal(["LAPTOP"], await en.Db.Products.Where(p => p.Name == "Laptop").Select(p => p.Sku).ToListAsync());
    }

    [Fact]
    public async Task BaseLanguageMode_AQueryReadsTheTranslation_AndIgnoreTranslationsReadsTheColumn()
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

        var translated = await ar.Db.Products.OrderBy(p => p.Sku).Select(p => p.Name).ToListAsync();
        Assert.Equal(["حاسوب", "Mouse"], translated);     // MOUSE falls back to the column

        // Valid SQL in this mode, and sometimes intended: the base column, never refused.
        var column = await ar.Db.Products.IgnoreTranslations().OrderBy(p => p.Sku).Select(p => p.Name).ToListAsync();
        Assert.Equal(["Laptop", "Mouse"], column);
    }
}
