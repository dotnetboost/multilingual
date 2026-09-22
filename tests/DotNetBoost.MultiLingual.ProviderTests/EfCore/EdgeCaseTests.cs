using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>
/// The less common ways EF Core queries and loads: bulk updates, automatic includes, inheritance,
/// set operations, raw SQL, lazy-loading proxies and compiled queries — all with plain EF Core.
/// </summary>
public sealed class EdgeCaseTests
{
    /// <summary>
    /// Category "books" (en Books, fr Livres) holding LAPTOP (en Laptop, fr Portable) and MOUSE (en
    /// Mouse); an article (en Notice, fr Avis) and a news item (en Launch / We launched, fr Lancement /
    /// Nous avons lancé).
    /// </summary>
    private static async Task SeedAsync<TContext>(EdgeShop<TContext> shop) where TContext : EdgeDbContextBase
    {
        await using var scope = await shop.ScopeInAsync("en");

        var books  = new EdgeCategory { Name = "Books" };
        var laptop = new EdgeProduct { Sku = "LAPTOP", Price = 10, Name = "Laptop", Category = books };
        var mouse  = new EdgeProduct { Sku = "MOUSE", Price = 5, Name = "Mouse", Category = books };
        var notice = new EdgeArticle { Title = "Notice" };
        var launch = new EdgeNews { Title = "Launch", Summary = "We launched" };

        scope.MultiLingual.For(books).Set("fr", c => c.Name, "Livres");
        scope.MultiLingual.For(laptop).Set("fr", p => p.Name, "Portable");
        scope.MultiLingual.For(notice).Set("fr", a => a.Title, "Avis");
        scope.MultiLingual.For(launch).Set("fr", new Dictionary<string, string?> { ["Title"] = "Lancement", ["Summary"] = "Nous avons lancé" });

        scope.Db.AddRange(laptop, mouse, notice, launch);
        await scope.Db.SaveChangesAsync();
    }

    // ================================================================== ExecuteUpdate

    [Fact]
    public async Task ExecuteUpdate_FiltersOnTheTranslatedValue_AndCanSetFromIt()
    {
        await using var shop = await EdgeShop<EdgeDbContext>.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var repriced = await scope.Db.Products.Where(p => p.Name == "Portable").ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 99));
        var renamed  = await scope.Db.Products.Where(p => p.Price == 99).ExecuteUpdateAsync(s => s.SetProperty(p => p.Sku, p => p.Name));

        Assert.Equal((1, 1), (repriced, renamed));
        Assert.Equal(["MOUSE", "Portable"], await scope.Db.Products.Select(p => p.Sku).OrderBy(s => s).ToListAsync());
    }

    [Fact]
    public async Task ExecuteUpdate_SettingATranslatableProperty_IsRefused()
    {
        await using var shop = await EdgeShop<EdgeDbContext>.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Db.Products.Where(p => p.Sku == "LAPTOP")
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "Ordinateur")));

        Assert.Contains("EdgeProduct.Name", ex.Message, StringComparison.Ordinal);

        // Refused before the database is touched — the seeded French translation is untouched.
        Assert.Equal("Portable", await scope.Db.Products.AsNoTracking()
            .Where(p => p.Sku == "LAPTOP").Select(p => p.Name).SingleAsync());
    }

    // ================================================================== automatic includes

    [Fact]
    public async Task AnAutomaticInclude_IsFilled_AndIgnoreAutoIncludesLeavesItOut()
    {
        await using var shop = await EdgeShop<EdgeDbContext>.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var laptop = await scope.Db.Products.AsNoTracking().SingleAsync(p => p.Sku == "LAPTOP");
        Assert.Equal(("Portable", "Livres"), (laptop.Name, laptop.Category!.Name));

        var bare = await scope.Db.Products.AsNoTracking().IgnoreAutoIncludes().SingleAsync(p => p.Sku == "LAPTOP");
        Assert.Equal("Portable", bare.Name);
        Assert.Null(bare.Category);
    }

    // ================================================================== inheritance

    [Fact]
    public async Task AQueryOverTheBaseType_FillsEachDerivedTypesOwnProperties()
    {
        await using var shop = await EdgeShop<EdgeDbContext>.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var articles = await scope.Db.Articles.AsNoTracking().OrderBy(a => a.Id).ToListAsync();

        Assert.Equal(["Avis", "Lancement"], articles.Select(a => a.Title));
        Assert.Equal("Nous avons lancé", Assert.IsType<EdgeNews>(articles[1]).Summary);
    }

    [Fact]
    public async Task ADerivedType_CanBeQueried_FilteredAndProjected_OnItsOwnProperties()
    {
        await using var shop = await EdgeShop<EdgeDbContext>.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var news = await scope.Db.Articles.OfType<EdgeNews>().AsNoTracking().SingleAsync(n => n.Summary == "Nous avons lancé");
        Assert.Equal(("Lancement", "Nous avons lancé"), (news.Title, news.Summary));

        var titles = await scope.Db.Articles.Where(a => a.Title!.StartsWith("Lan")).Select(a => a.Title).ToListAsync();
        Assert.Equal(["Lancement"], titles);
    }

    // ================================================================== set operations

    [Fact]
    public async Task ConcatAndUnion_OfEntityQueries_AreFilled()
    {
        await using var shop = await EdgeShop<LazyEdgeDbContext>.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var concat = await scope.Db.Products.AsNoTracking().Where(p => p.Sku == "LAPTOP")
            .Concat(scope.Db.Products.AsNoTracking().Where(p => p.Sku == "MOUSE"))
            .ToListAsync();

        var union = await scope.Db.Products.AsNoTracking().Where(p => p.Name == "Portable")
            .Union(scope.Db.Products.AsNoTracking().Where(p => p.Name == "Mouse"))
            .ToListAsync();

        Assert.Equal(["Mouse", "Portable"], concat.Select(p => p.Name).Order());
        Assert.Equal(["Mouse", "Portable"], union.Select(p => p.Name).Order());
    }

    // ================================================================== raw SQL

    [Fact]
    public async Task AQueryComposedOnFromSql_IsTranslatedAndFilled()
    {
        await using var shop = await EdgeShop<LazyEdgeDbContext>.StartAsync();
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var products = await scope.Db.Products
            .FromSql($"SELECT * FROM \"Products\"")
            .AsNoTracking()
            .Where(p => p.Name == "Portable")
            .ToListAsync();

        Assert.Equal("Portable", Assert.Single(products).Name);
    }

    // ================================================================== lazy loading

    [Fact]
    public async Task LazyLoadedNavigations_AreFilled()
    {
        await using var shop = await EdgeShop<LazyEdgeDbContext>.StartAsync(o => o.UseLazyLoadingProxies());
        await SeedAsync(shop);
        await using var scope = await shop.ScopeInAsync("fr");

        var laptop = await scope.Db.Products.SingleAsync(p => p.Sku == "LAPTOP");
        Assert.NotEqual(typeof(EdgeProduct), laptop.GetType());   // a proxy
        Assert.Equal("Portable", laptop.Name);

        var category = laptop.Category!;                           // loaded on first access
        Assert.Equal("Livres", category.Name);
        Assert.Equal(["Mouse", "Portable"], category.Products.Select(p => p.Name).Order());
    }

    [Fact]
    public async Task AProxy_IsReadAndSavedUnderItsEntitysName()
    {
        await using var shop = await EdgeShop<LazyEdgeDbContext>.StartAsync(o => o.UseLazyLoadingProxies());
        await SeedAsync(shop);

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            var laptop = await scope.Db.Products.SingleAsync(p => p.Sku == "LAPTOP");

            var stored = await scope.MultiLingual.For(laptop).GetAllAsync();
            Assert.Equal("Portable", stored["fr"]["Name"]);

            laptop.Name  = "Ordinateur";
            laptop.Price = 20;
            await scope.Db.SaveChangesAsync();
        }

        await using var read = await shop.ScopeInAsync("fr");
        var again = await read.Db.Products.AsNoTracking().SingleAsync(p => p.Sku == "LAPTOP");
        Assert.Equal(("Ordinateur", 20), (again.Name, again.Price));
    }

    // ================================================================== compiled queries

    private static readonly Func<EdgeDbContext, string, Task<EdgeProduct>> BySku =
        EF.CompileAsyncQuery((EdgeDbContext db, string sku) => db.Products.AsNoTracking().Single(p => p.Sku == sku));

    private static readonly Func<EdgeDbContext, string, IAsyncEnumerable<string>> SkusByName =
        EF.CompileAsyncQuery((EdgeDbContext db, string name) => db.Products.Where(p => p.Name == name).Select(p => p.Sku));

    [Fact]
    public async Task CompiledQueries_AnswerEachExecutionInItsScopesLanguage()
    {
        await using var shop = await EdgeShop<EdgeDbContext>.StartAsync();
        await SeedAsync(shop);

        foreach (var (language, name) in new[] { ("fr", "Portable"), ("en", "Laptop"), ("fr", "Portable") })
        {
            await using var scope = await shop.ScopeInAsync(language);

            Assert.Equal(name, (await BySku(scope.Db, "LAPTOP")).Name);

            var skus = new List<string>();
            await foreach (var sku in SkusByName(scope.Db, name))
                skus.Add(sku);

            Assert.Equal(["LAPTOP"], skus);
        }
    }
}
