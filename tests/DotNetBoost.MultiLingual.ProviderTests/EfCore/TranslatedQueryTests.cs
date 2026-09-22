using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>Plain LINQ over translated text with <c>AsTranslated</c>, run by the database.</summary>
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

        var skus = await SkusAsync(scope.Db.Products.AsTranslated(scope.Db).Where(p => p.Name!.StartsWith("Po")));

        Assert.Equal(["LAPTOP"], skus);
    }

    [Fact]
    public async Task Where_FallsBackToTheDefaultLanguage_LikeAReadDoes()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        // MOUSE has no French name, so a read shows the English one — and a filter matches it.
        Assert.Equal(["MOUSE"], await SkusAsync(scope.Db.Products.AsTranslated(scope.Db).Where(p => p.Name == "Mouse")));

        // LAPTOP does have a French name, so its English one is not what a read shows.
        Assert.Empty(await SkusAsync(scope.Db.Products.AsTranslated(scope.Db).Where(p => p.Name == "Laptop")));
    }

    [Fact]
    public async Task Where_NothingTranslated_IsNull()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        Assert.Equal(["BARE"], await SkusAsync(scope.Db.Products.AsTranslated(scope.Db).Where(p => p.Name == null)));
    }

    [Fact]
    public async Task Where_WithAnExplicitLanguage_IgnoresTheCurrentOne()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var skus = await SkusAsync(scope.Db.Products.AsTranslated(scope.Db, languageCode: "EN").Where(p => p.Name == "Laptop"));

        Assert.Equal(["LAPTOP"], skus);
    }

    [Fact]
    public async Task Where_WithNoLanguageResolved_UsesTheDefault()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = shop.Scope();

        Assert.Equal(["LAPTOP"], await SkusAsync(scope.Db.Products.AsTranslated(scope.Db).Where(p => p.Name == "Laptop")));
    }

    [Fact]
    public async Task OrdinaryFilters_WorkBeforeAndAfter_AndAreNotTranslated()
    {
        await using var shop = await Shop.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var before = await SkusAsync(scope.Db.Products
            .Where(p => p.Sku != "KEYBOARD")
            .AsTranslated(scope.Db)
            .Where(p => p.Name!.StartsWith("Po") || p.Name.StartsWith("Cl")));

        var after = await SkusAsync(scope.Db.Products
            .AsTranslated(scope.Db)
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
            scope.Db.Products.AsTranslated(scope.Db, language).Where(p => p.Name != null);

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
            .AsTranslated(scope.Db)
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
            .AsTranslated(scope.Db)
            .Where(p => p.Name!.EndsWith("er") || p.Name.EndsWith("le"))
            .OrderBy(p => p.Name)
            .ToLocalizedListAsync();

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
        var ids = await scope.Db.Documents.AsTranslated(scope.Db).Where(d => d.Title == "دليل").Select(d => d.Id).ToListAsync();

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
            return await scope.Db.Products.AsTranslated(scope.Db)
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
    public async Task Sql_HasOneParameterPerLanguage_AndLiteralNames()
    {
        await using var shop = await Shop.StartAsync();
        await using var scope = await shop.ScopeInAsync("fr");

        var sql = scope.Db.Products
            .AsTranslated(scope.Db)
            .Select(p => new { p.Name, p.Description, Category = p.Category!.Name })
            .ToQueryString();

        var parameters = sql.Split('\n').Where(line => line.StartsWith(".param set", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, parameters.Count);                 // fr and its fallback en — not 18
        Assert.Contains(parameters, p => p.EndsWith("'fr'", StringComparison.Ordinal));
        Assert.Contains(parameters, p => p.EndsWith("'en'", StringComparison.Ordinal));
        Assert.Contains("'ShopProduct'", sql, StringComparison.Ordinal);
        Assert.Contains("'Description'", sql, StringComparison.Ordinal);
    }

    // ================================================================== misuse

    [Fact]
    public async Task UnknownLanguage_Throws()
    {
        await using var shop  = await Shop.StartAsync();
        await using var scope = shop.Scope();

        Assert.Throws<UnknownLanguageException>(() => scope.Db.Products.AsTranslated(scope.Db, languageCode: "de"));
    }

    [Fact]
    public async Task AsTranslated_Twice_IsRefused()
    {
        await using var shop  = await Shop.StartAsync();
        await using var scope = await shop.ScopeInAsync("fr");

        var twice = scope.Db.Products.AsTranslated(scope.Db).Where(p => p.Price > 0).AsTranslated(scope.Db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => twice.ToListAsync());
        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
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
            .AsTranslated(scope.Db)
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
            .AsTranslated(scope.Db)
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
            .AsTranslated(scope.Db, "en")
            .Where(p => p.Name != null)
            .OrderByDescending(p => p.Name)
            .Select(p => p.Name)
            .ToListAsync();

        Assert.Equal(["Mouse", "Laptop"], names);
    }

    [Fact]
    public async Task Include_AfterAsTranslated_StillLoadsTheNavigation()
    {
        await using var shop = await Shop.StartAsync();
        await SeedCatalogueAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var laptop = await scope.Db.Products.AsNoTracking()
            .AsTranslated(scope.Db)
            .Include(p => p.Category)
            .Where(p => p.Name == "Portable")
            .FirstLocalizedOrDefaultAsync();

        Assert.Equal("Portable", laptop!.Name);
        Assert.Equal("Livres", laptop.Category!.Name);
    }

    [Fact]
    public async Task PlainProjection_OfAColumnlessProperty_IsRefused_InsteadOfReturningNull()
    {
        await using var shop = await Shop.StartAsync();
        await SeedCatalogueAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Db.Products.Select(p => new { p.Sku, p.Name }).ToListAsync());

        Assert.Contains("ShopProduct.Name", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AsTranslated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainFilter_OnAColumnlessProperty_IsRefused_WithTheSameClearMessage()
    {
        await using var shop  = await Shop.StartAsync();
        await using var scope = await shop.ScopeInAsync("fr");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Db.Categories.Where(c => c.Products.Any(p => p.Name == "Portable")).ToListAsync());

        Assert.Contains("ShopProduct.Name", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AsTranslated", ex.Message, StringComparison.Ordinal);
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
        Assert.Equal(["LAPTOP"], await ar.Db.Products.AsTranslated(ar.Db).Where(p => p.Name == "حاسوب").Select(p => p.Sku).ToListAsync());
        Assert.Equal(["MOUSE"], await ar.Db.Products.AsTranslated(ar.Db).Where(p => p.Name == "Mouse").Select(p => p.Sku).ToListAsync());

        // "Mouse" (column) sorts before "حاسوب".
        Assert.Equal(["MOUSE", "LAPTOP"], await ar.Db.Products.AsTranslated(ar.Db).OrderBy(p => p.Name).Select(p => p.Sku).ToListAsync());

        await using var en = await shop.ScopeInAsync("en");
        Assert.Equal(["LAPTOP"], await en.Db.Products.AsTranslated(en.Db).Where(p => p.Name == "Laptop").Select(p => p.Sku).ToListAsync());
    }

    [Fact]
    public async Task BaseLanguageMode_AsTranslated_ReadsTheTranslation_WhereAPlainSelectReadsTheColumn()
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

        var translated = await ar.Db.Products.AsTranslated(ar.Db).OrderBy(p => p.Sku).Select(p => p.Name).ToListAsync();
        Assert.Equal(["حاسوب", "Mouse"], translated);     // MOUSE falls back to the column

        // Valid SQL in this mode, and sometimes intended: the base column, never refused.
        var column = await ar.Db.Products.OrderBy(p => p.Sku).Select(p => p.Name).ToListAsync();
        Assert.Equal(["Laptop", "Mouse"], column);
    }
}
