using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>
/// An editor's read and write of every language at once — <c>SetAll</c> and
/// <c>GetAllAsync(includeBase: true)</c> — in base-language mode, under a request in another
/// language, which is never changed.
/// </summary>
public sealed class AllLanguagesTests
{
    private static Dictionary<string, IReadOnlyDictionary<string, string?>> Names(params (string Code, string? Name)[] names) =>
        names.ToDictionary(n => n.Code, n => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["Name"] = n.Name });

    private static async Task<List<(string Language, string? Value)>> StoredNamesAsync(BaseShop shop) =>
        (await shop.StoredTranslationsAsync()).Where(t => t.PropertyName == "Name").Select(t => (t.LanguageCode, t.Value)).ToList();

    private static async Task<int> SeedAsync(BaseShop shop)
    {
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using var scope = await shop.ScopeInAsync("ar");
        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
        product.Name = "حاسوب";
        await scope.Db.SaveChangesAsync();

        return id;
    }

    // ----------------------------------------------------------------- SetAll

    [Fact]
    public async Task SetAll_UnderAnotherLanguage_WritesTheColumn_AndEveryOtherLanguageAsATranslation()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await SeedAsync(shop);

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            scope.Get<IMultiLingualManager>().For(product).SetAll(Names(("en", "Laptop Pro"), ("ar", "حاسوب برو"), ("fr", "Portable Pro")));
            await scope.Db.SaveChangesAsync();

            Assert.Equal("ar", scope.Get<ILanguageContext>().Current.Code);
            Assert.Equal("Laptop Pro", product.Name);   // the properties hold the base language now
        }

        Assert.Equal("Laptop Pro", await shop.StoredColumnNameAsync(id));
        Assert.Equal<(string, string?)>([("ar", "حاسوب برو"), ("fr", "Portable Pro")], await StoredNamesAsync(shop));
    }

    /// <summary>Trap 1: under ar the save treats the properties as Arabic; the base text must not become that.</summary>
    [Fact]
    public async Task SetAll_WithOnlyTheBaseLanguage_UnderAnotherLanguage_StoresNoTranslation()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            scope.Get<IMultiLingualManager>().For(product).SetAll(Names(("en", "Laptop Pro")));
            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal("Laptop Pro", await shop.StoredColumnNameAsync(id));
        Assert.Empty(await shop.StoredTranslationsAsync());
    }

    [Fact]
    public async Task SetAll_SavedAgain_WritesNothingMore_AndALaterEditIsThatLanguagesAgain()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            scope.Get<IMultiLingualManager>().For(product).SetAll(Names(("en", "Laptop Pro"), ("ar", "حاسوب برو")));
            await scope.Db.SaveChangesAsync();

            product.Price = 20;
            await scope.Db.SaveChangesAsync();
            Assert.Equal("Laptop Pro", await shop.StoredColumnNameAsync(id));
            Assert.Equal<(string, string?)>([("ar", "حاسوب برو")], await StoredNamesAsync(shop));

            // Assigned directly, under ar: Arabic again, as any edit in this language is.
            product.Name = "حاسوب ٢";
            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal("Laptop Pro", await shop.StoredColumnNameAsync(id));
        Assert.Equal<(string, string?)>([("ar", "حاسوب ٢")], await StoredNamesAsync(shop));
    }

    [Fact]
    public async Task SetAll_ANullBaseValue_EmptiesTheColumn()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            scope.Get<IMultiLingualManager>().For(product).SetAll(Names(("en", null)));
            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal("", await shop.StoredColumnNameAsync(id));
        Assert.Empty(await shop.StoredTranslationsAsync());
    }

    [Fact]
    public async Task SetAll_RefusesAnUnknownLanguageOrProperty_AndQueuesNothing()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product  = await scope.Db.Products.SingleAsync(p => p.Id == id);
            var accessor = scope.Get<IMultiLingualManager>().For(product);

            Assert.Throws<LanguageNotFoundException>(() => accessor.SetAll(Names(("en", "Laptop Pro"), ("ar", "حاسوب"), ("de", "Rechner"))));
            Assert.Throws<ArgumentException>(() => accessor.SetAll(new Dictionary<string, IReadOnlyDictionary<string, string?>>
            {
                ["ar"] = new Dictionary<string, string?> { ["Name"] = "حاسوب" },
                ["en"] = new Dictionary<string, string?> { ["Sku"] = "NEW" },
            }));

            Assert.Equal("Laptop", product.Name);
            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal("Laptop", await shop.StoredColumnNameAsync(id));
        Assert.Empty(await shop.StoredTranslationsAsync());
    }

    // ----------------------------------------------------------------- create

    [Fact]
    public async Task Create_UnderAnotherLanguage_ThroughSetAll_IsAllowed()
    {
        await using var shop = await BaseShop.StartAsync();

        int id;
        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = new ShopProduct { Sku = "MOUSE" };
            scope.Get<IMultiLingualManager>().For(product).SetAll(Names(("en", "Mouse"), ("ar", "فأرة")));
            scope.Db.Products.Add(product);
            await scope.Db.SaveChangesAsync();
            id = product.Id;
        }

        Assert.Equal("Mouse", await shop.StoredColumnNameAsync(id));
        Assert.Equal<(string, string?)>([("ar", "فأرة")], await StoredNamesAsync(shop));
    }

    [Fact]
    public async Task Create_UnderAnotherLanguage_WithAValueNotFromSetAll_IsStillRefused()
    {
        await using var shop  = await BaseShop.StartAsync();
        await using var scope = await shop.ScopeInAsync("ar");

        var product = new ShopProduct { Sku = "MOUSE" };
        scope.Get<IMultiLingualManager>().For(product).SetAll(Names(("en", "Mouse")));
        product.Description = "وصف";
        scope.Db.Products.Add(product);

        await Assert.ThrowsAsync<BaseLanguageRequiredException>(() => scope.Db.SaveChangesAsync());
    }

    // ----------------------------------------------------------------- GetAllAsync(includeBase: true)

    [Fact]
    public async Task GetAllIncludingBase_UnderAnotherLanguage_ReadsTheColumn_NotTheLanguageServed()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await SeedAsync(shop);

        await using var scope = await shop.ScopeInAsync("ar");
        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
        Assert.Equal("حاسوب", product.Name);

        var all = await scope.Get<IMultiLingualManager>().For(product).GetAllAsync(includeBase: true);

        Assert.Equal("Laptop", all["en"]["Name"]);
        Assert.Equal("Laptop", all["EN"]["Name"]);   // one map, case-insensitive
        Assert.Equal("حاسوب", all["ar"]["Name"]);
        Assert.Null(all["en"]["Description"]);
    }

    /// <summary>Trap 2: after a save under ar, EF's original value is the Arabic text; the column is not.</summary>
    [Fact]
    public async Task GetAllIncludingBase_AfterASaveInAnotherLanguage_StillReadsTheColumn()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await SeedAsync(shop);

        await using var scope = await shop.ScopeInAsync("ar");
        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
        product.Name = "حاسوب جديد";
        await scope.Db.SaveChangesAsync();

        var all = await scope.Get<IMultiLingualManager>().For(product).GetAllAsync(includeBase: true);

        Assert.Equal("Laptop", all["en"]["Name"]);
        Assert.Equal("حاسوب جديد", all["ar"]["Name"]);
    }

    [Fact]
    public async Task GetAllIncludingBase_OnAnUntrackedEntity_ReadsTheColumn()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await SeedAsync(shop);

        await using var scope = await shop.ScopeInAsync("ar");
        var product = await scope.Db.Products.AsNoTracking().SingleAsync(p => p.Id == id);

        var all = await scope.Get<IMultiLingualManager>().For(product).GetAllAsync(includeBase: true);

        Assert.Equal("Laptop", all["en"]["Name"]);
        Assert.Equal("حاسوب", all["ar"]["Name"]);
    }

    /// <summary>What is stored, as for every other language: a SetAll not saved yet does not show.</summary>
    [Fact]
    public async Task GetAllIncludingBase_ShowsTheStoredColumn_NotAnUnsavedSetAll()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await SeedAsync(shop);

        await using var scope = await shop.ScopeInAsync("ar");
        var product  = await scope.Db.Products.SingleAsync(p => p.Id == id);
        var accessor = scope.Get<IMultiLingualManager>().For(product);
        accessor.SetAll(Names(("en", "Laptop Pro"), ("ar", "حاسوب برو")));

        var all = await accessor.GetAllAsync(includeBase: true);

        Assert.Equal("Laptop", all["en"]["Name"]);
        Assert.Equal("حاسوب", all["ar"]["Name"]);
    }

    [Fact]
    public async Task GetAll_WithoutIncludeBase_StillLeavesTheBaseLanguageOut()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await SeedAsync(shop);

        await using var scope = await shop.ScopeInAsync("ar");
        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);

        Assert.Equal(["ar"], (await scope.Get<IMultiLingualManager>().For(product).GetAllAsync(includeBase: false)).Keys);
        Assert.Equal(["ar"], (await scope.Get<IMultiLingualManager>().For(product).GetAllAsync()).Keys);
    }

    /// <summary>The sample's editor, without switching the request's language: read every box, write every box, read again.</summary>
    [Fact]
    public async Task AnEditorRoundTrip_UnderAnotherLanguage_NeverChangesTheCurrentLanguage()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await SeedAsync(shop);

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var multiLingual = scope.Get<IMultiLingualManager>();
            var product      = await scope.Db.Products.SingleAsync(p => p.Id == id);

            var boxes = (await multiLingual.For(product).GetAllAsync(includeBase: true))
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(v => v.Key, v => v.Value));
            boxes["en"]["Name"] = boxes["en"]["Name"] + " Pro";
            boxes["fr"]         = new Dictionary<string, string?> { ["Name"] = "Portable Pro" };

            multiLingual.For(product).SetAll(boxes.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string?>)kv.Value));
            await scope.Db.SaveChangesAsync();

            Assert.Equal("ar", multiLingual.CurrentLanguage.Code);
        }

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            var all     = await scope.Get<IMultiLingualManager>().For(product).GetAllAsync(includeBase: true);

            Assert.Equal("Laptop Pro", all["en"]["Name"]);
            Assert.Equal("حاسوب", all["ar"]["Name"]);
            Assert.Equal("Portable Pro", all["fr"]["Name"]);
            Assert.Equal("حاسوب", product.Name);
        }
    }

    // ----------------------------------------------------------------- outside base-language mode

    [Fact]
    public async Task OutsideBaseLanguageMode_SetAllIsSetPerLanguage_AndIncludeBaseAddsNothing()
    {
        await using var shop = await Shop.StartAsync();

        int id;
        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = new ShopProduct { Sku = "MOUSE" };
            scope.MultiLingual.For(product).SetAll(Names(("en", "Mouse"), ("ar", "فأرة")));
            scope.Db.Products.Add(product);
            await scope.Db.SaveChangesAsync();
            id = product.Id;
        }

        Assert.Equal(
            [("ar", "فأرة"), ("en", "Mouse")],
            (await shop.StoredTranslationsAsync()).Where(t => t.PropertyName == "Name").Select(t => (t.LanguageCode, t.Value!)).ToList());

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            var withBase = await scope.MultiLingual.For(product).GetAllAsync(includeBase: true);
            var without  = await scope.MultiLingual.For(product).GetAllAsync();

            Assert.Equal(without.Keys.Order(), withBase.Keys.Order());
        }
    }
}
