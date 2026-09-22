using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>
/// Same entities as <see cref="ShopDbContext"/>, but the mapping follows the engine's
/// configuration. Its own context type because EF caches a model per context type: two
/// differently configured contexts of one type would share the first model built.
/// </summary>
public sealed class BaseShopDbContext(DbContextOptions<BaseShopDbContext> options)
    : DbContext(options), IMultiLingualDbContext
{
    public DbSet<Translation> Translations => Set<Translation>();
    public DbSet<Language> Languages => Set<Language>();
    public DbSet<ShopProduct> Products => Set<ShopProduct>();
    public DbSet<ShopCategory> Categories => Set<ShopCategory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShopProduct>().Property(p => p.Price).HasConversion<double>();
        modelBuilder.Ignore<ShopDocument>();
        modelBuilder.ApplyMultiLingual(this);
    }
}

/// <summary>The provider wired as an application wires it, with <c>BaseLanguage = "en"</c>.</summary>
internal sealed class BaseShop : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _root;

    private BaseShop(SqliteConnection connection, ServiceProvider root)
    {
        _connection = connection;
        _root       = root;
    }

    public static async Task<BaseShop> StartAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddMultiLingual(o => o.BaseLanguage = "en")
            .UseEntityFrameworkCore<BaseShopDbContext>()
            .Build();

        services.AddDbContext<BaseShopDbContext>((sp, options) => options.UseSqlite(connection).UseMultiLingual(sp));

        var shop = new BaseShop(connection, services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));

        await using var scope = shop.Scope();
        await scope.Db.Database.EnsureCreatedAsync();
        var languages = scope.Get<ILanguageManager>();
        await languages.CreateAsync(new NewLanguage { Code = "en", Name = "English" });
        await languages.CreateAsync(new NewLanguage { Code = "ar", Name = "Arabic" });
        await languages.CreateAsync(new NewLanguage { Code = "fr", Name = "French" });

        return shop;
    }

    public BaseShopScope Scope() => new(_root.CreateAsyncScope());

    public async Task<BaseShopScope> ScopeInAsync(string languageCode)
    {
        var scope = Scope();
        await scope.Get<ILanguageContext>().UseAsync(languageCode);
        return scope;
    }

    /// <summary>
    /// The raw <c>Products.Name</c> column. Every query is translated, so this one opts out — and
    /// reads what the table itself holds.
    /// </summary>
    public async Task<string?> StoredColumnNameAsync(int productId)
    {
        await using var scope = Scope();
        var product = await scope.Db.Products.AsNoTracking().IgnoreTranslations().SingleAsync(p => p.Id == productId);
        return product.Name;
    }

    public async Task<List<Translation>> StoredTranslationsAsync()
    {
        await using var scope = Scope();
        return await scope.Db.Translations.AsNoTracking()
            .OrderBy(t => t.EntityId).ThenBy(t => t.PropertyName).ThenBy(t => t.LanguageCode)
            .ToListAsync();
    }

    /// <summary>Creates a product in the base language, as every create must be.</summary>
    public async Task<int> CreateProductAsync(string sku, string name, string? description = null)
    {
        await using var scope = await ScopeInAsync("en");
        var product = new ShopProduct { Sku = sku, Price = 10, Name = name, Description = description };
        scope.Db.Products.Add(product);
        await scope.Db.SaveChangesAsync();
        return product.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

internal sealed class BaseShopScope(AsyncServiceScope scope) : IAsyncDisposable
{
    public T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public BaseShopDbContext Db => Get<BaseShopDbContext>();

    public ITranslationManager Translations => Get<ITranslationManager>();

    public ValueTask DisposeAsync() => scope.DisposeAsync();
}

/// <summary>
/// Base-language mode: the base language lives in each entity's own table, every other language
/// is a translation row, and a missing translation falls back to the column.
/// </summary>
public sealed class BaseLanguageTests
{
    // ----------------------------------------------------------------- mapping

    [Fact]
    public async Task TranslatableProperties_KeepTheirColumn()
    {
        await using var shop  = await BaseShop.StartAsync();
        await using var scope = shop.Scope();
        var model = scope.Db.Model;

        Assert.NotNull(model.FindEntityType(typeof(ShopProduct))!.FindProperty(nameof(ShopProduct.Name)));
        Assert.NotNull(model.FindEntityType(typeof(ShopCategory))!.FindProperty(nameof(ShopCategory.Name)));
    }

    // ----------------------------------------------------------------- create

    [Fact]
    public async Task Create_InTheBaseLanguage_WritesTheColumn_AndNoTranslationRow()
    {
        await using var shop = await BaseShop.StartAsync();

        var id = await shop.CreateProductAsync("LAPTOP", "Laptop", "Light");

        Assert.Equal("Laptop", await shop.StoredColumnNameAsync(id));
        Assert.Empty(await shop.StoredTranslationsAsync());
    }

    [Fact]
    public async Task Create_InAnotherLanguage_IsRefused()
    {
        await using var shop  = await BaseShop.StartAsync();
        await using var scope = await shop.ScopeInAsync("ar");

        scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Price = 5, Name = "فأرة" });

        var ex = await Assert.ThrowsAsync<BaseLanguageRequiredException>(() => scope.Db.SaveChangesAsync());
        Assert.Equal("en", ex.BaseLanguage);
        Assert.Equal("ar", ex.RequestedLanguage);
    }

    [Fact]
    public async Task Create_WithNoLanguageResolved_IsAllowed_ForSeedersAndJobs()
    {
        await using var shop  = await BaseShop.StartAsync();
        await using var scope = shop.Scope();

        var product = new ShopProduct { Sku = "SEED", Price = 1, Name = "Seeded" };
        scope.Db.Products.Add(product);
        await scope.Db.SaveChangesAsync();

        Assert.Equal("Seeded", await shop.StoredColumnNameAsync(product.Id));
    }

    // ----------------------------------------------------------------- update

    [Fact]
    public async Task Update_InAnotherLanguage_WritesATranslationRow_AndLeavesTheBaseColumn()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            product.Name = "حاسوب";
            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal("Laptop", await shop.StoredColumnNameAsync(id));

        var row = Assert.Single(await shop.StoredTranslationsAsync());
        Assert.Equal(("Name", "ar", "حاسوب"), (row.PropertyName, row.LanguageCode, row.Value));
    }

    [Fact]
    public async Task Update_InTheBaseLanguage_ChangesTheColumn_AndStillWritesNoTranslationRow()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using (var scope = await shop.ScopeInAsync("en"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            product.Name = "Notebook";
            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal("Notebook", await shop.StoredColumnNameAsync(id));
        Assert.Empty(await shop.StoredTranslationsAsync());
    }

    [Fact]
    public async Task Update_InAnotherLanguage_OnADetachedEntity_StillLeavesTheBaseColumn()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            // Update() marks every property modified, so the base column is only safe because
            // the interceptor excludes translatable properties rather than restoring them.
            scope.Db.Products.Update(new ShopProduct { Id = id, Sku = "LAPTOP", Price = 99, Name = "Portable" });
            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal("Laptop", await shop.StoredColumnNameAsync(id));
        Assert.Equal("Portable", Assert.Single(await shop.StoredTranslationsAsync()).Value);
    }

    // ----------------------------------------------------------------- read

    [Fact]
    public async Task Read_InTheBaseLanguage_ReturnsTheColumn()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using var scope = await shop.ScopeInAsync("en");
        var product = await scope.Db.Products.Where(p => p.Id == id).FirstOrDefaultAsync();

        Assert.Equal("Laptop", product!.Name);
    }

    [Fact]
    public async Task Read_InAnotherLanguage_ReturnsTheTranslation()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var toUpdate = await scope.Db.Products.SingleAsync(p => p.Id == id);
            toUpdate.Name = "حاسوب";
            await scope.Db.SaveChangesAsync();
        }

        await using var read = await shop.ScopeInAsync("ar");
        var product = await read.Db.Products.Where(p => p.Id == id).FirstOrDefaultAsync();

        Assert.Equal("حاسوب", product!.Name);
    }

    [Fact]
    public async Task Read_InALanguageWithNoTranslation_FallsBackToTheBaseColumn()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop", "Light");

        await using var scope = await shop.ScopeInAsync("fr");
        var product = await scope.Db.Products.Where(p => p.Id == id).FirstOrDefaultAsync();

        Assert.Equal("Laptop", product!.Name);
        Assert.Equal("Light", product.Description);
    }

    [Fact]
    public async Task Read_AfterAFallback_DoesNotPromoteTheBaseValueIntoATranslation()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            var product = await scope.Db.Products.Where(p => p.Id == id).FirstOrDefaultAsync();
            Assert.Equal("Laptop", product!.Name);   // the base column, as a fallback

            product.Price = 20;                       // touch something else entirely
            await scope.Db.SaveChangesAsync();
        }

        Assert.Empty(await shop.StoredTranslationsAsync());
        Assert.Equal("Laptop", await shop.StoredColumnNameAsync(id));
    }

    [Fact]
    public async Task Update_InAnotherLanguage_TouchingOnlyAnOrdinaryColumn_WritesNoTranslation()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop", "Light");

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            // A write endpoint loads without hydrating, so Name still holds the English column.
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            Assert.Equal("Laptop", product.Name);

            product.Price = 20;
            await scope.Db.SaveChangesAsync();
        }

        // The English name must not have been filed as the Arabic one.
        Assert.Empty(await shop.StoredTranslationsAsync());
        Assert.Equal("Laptop", await shop.StoredColumnNameAsync(id));
    }

    [Fact]
    public async Task Update_InAnotherLanguage_WritesOnlyThePropertiesThatWereTouched()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop", "Light");

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            product.Name = "حاسوب";     // Description deliberately left alone
            await scope.Db.SaveChangesAsync();
        }

        var row = Assert.Single(await shop.StoredTranslationsAsync());
        Assert.Equal(("Name", "ar", "حاسوب"), (row.PropertyName, row.LanguageCode, row.Value));
    }

    // ----------------------------------------------------------------- the default language moves, content does not

    [Fact]
    public async Task AJob_ReadingInADefaultOtherThanTheBase_NeverWritesItIntoTheBaseColumn()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            (await scope.Db.Products.SingleAsync(p => p.Id == id)).Name = "حاسوب";
            await scope.Db.SaveChangesAsync();
        }

        await using (var scope = shop.Scope())
            await scope.Get<ILanguageManager>().SetDefaultAsync("ar");

        // A background job: no language resolved, so it reads in the default — Arabic. It loads
        // the product tracked (which fills Name with the Arabic text), changes only the price and
        // saves.
        await using (var job = shop.Scope())
        {
            var product = await job.Db.Products.SingleAsync(p => p.Id == id);
            Assert.Equal("حاسوب", product.Name);

            product.Price = 99;
            await job.Db.SaveChangesAsync();
        }

        Assert.Equal("Laptop", await shop.StoredColumnNameAsync(id));
        Assert.Equal("حاسوب", Assert.Single(await shop.StoredTranslationsAsync()).Value);
    }

    [Fact]
    public async Task ChangingTheDefaultLanguage_MovesNoStoredContent()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            product.Name = "حاسوب";
            await scope.Db.SaveChangesAsync();
        }

        await using (var scope = shop.Scope())
            await scope.Get<ILanguageManager>().SetDefaultAsync("ar");

        // Nothing moved: the base language is still the column, Arabic is still a row.
        Assert.Equal("Laptop", await shop.StoredColumnNameAsync(id));
        Assert.Equal("ar", Assert.Single(await shop.StoredTranslationsAsync()).LanguageCode);

        // English still reads English, even though Arabic is now the default.
        await using (var scope = await shop.ScopeInAsync("en"))
        {
            var product = await scope.Db.Products.Where(p => p.Id == id).FirstOrDefaultAsync();
            Assert.Equal("Laptop", product!.Name);
        }

        // French has no translation, so it takes the new default before the base column.
        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            var product = await scope.Db.Products.Where(p => p.Id == id).FirstOrDefaultAsync();
            Assert.Equal("حاسوب", product!.Name);
        }
    }

    // ----------------------------------------------------------------- the base language is protected

    [Fact]
    public async Task BaseLanguage_CannotBeDeleted()
    {
        await using var shop  = await BaseShop.StartAsync();
        await using var scope = shop.Scope();
        var languages = scope.Get<ILanguageManager>();

        await languages.SetDefaultAsync("ar");   // remove the "it is the default" objection

        var ex = await Assert.ThrowsAsync<LanguageConflictException>(() => languages.DeleteAsync("en"));
        Assert.Contains("base language", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BaseLanguage_CannotBeDeactivated()
    {
        await using var shop  = await BaseShop.StartAsync();
        await using var scope = shop.Scope();
        var languages = scope.Get<ILanguageManager>();

        await languages.SetDefaultAsync("ar");

        await Assert.ThrowsAsync<LanguageConflictException>(() =>
            languages.UpdateAsync("en", new LanguageUpdate { Name = "English", IsActive = false }));
    }

    [Fact]
    public async Task Staging_TheBaseLanguage_IsRefused()
    {
        await using var shop  = await BaseShop.StartAsync();
        await using var scope = shop.Scope();

        var product = new ShopProduct { Sku = "X", Price = 1 };

        Assert.Throws<ArgumentException>(() =>
            scope.Translations.Stage(product, "en", new Dictionary<string, string?> { ["Name"] = "Laptop" }));
    }

    [Fact]
    public async Task Staging_AnotherLanguage_OnCreate_StillWorks()
    {
        await using var shop = await BaseShop.StartAsync();

        int id;
        await using (var scope = await shop.ScopeInAsync("en"))
        {
            var product = new ShopProduct { Sku = "LAPTOP", Price = 10, Name = "Laptop" };
            scope.Translations.Stage(product, "ar", new Dictionary<string, string?> { ["Name"] = "حاسوب" });
            scope.Db.Products.Add(product);
            await scope.Db.SaveChangesAsync();
            id = product.Id;
        }

        Assert.Equal("Laptop", await shop.StoredColumnNameAsync(id));
        Assert.Equal(("ar", "حاسوب"), Assert.Single(await shop.StoredTranslationsAsync()) is { } r ? (r.LanguageCode, r.Value) : default);
    }

    // ----------------------------------------------------------------- delete

    [Fact]
    public async Task Delete_RemovesTheRowAndItsTranslations()
    {
        await using var shop = await BaseShop.StartAsync();
        var id = await shop.CreateProductAsync("LAPTOP", "Laptop");

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            product.Name = "حاسوب";
            await scope.Db.SaveChangesAsync();
        }

        await using (var scope = await shop.ScopeInAsync("en"))
        {
            scope.Db.Products.Remove(await scope.Db.Products.SingleAsync(p => p.Id == id));
            await scope.Db.SaveChangesAsync();
        }

        Assert.Empty(await shop.StoredTranslationsAsync());
    }
}

/// <summary>
/// A context mapped for table-only mode while the engine is configured with a base language —
/// the misconfiguration that would silently write the authored language into the base column.
/// </summary>
public sealed class MismatchedDbContext(DbContextOptions<MismatchedDbContext> options)
    : DbContext(options), IMultiLingualDbContext
{
    public DbSet<Translation> Translations => Set<Translation>();
    public DbSet<Language> Languages => Set<Language>();
    public DbSet<ShopProduct> Products => Set<ShopProduct>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShopProduct>().Property(p => p.Price).HasConversion<double>();
        modelBuilder.Ignore<ShopCategory>();
        modelBuilder.Ignore<ShopDocument>();

        // Deliberately the overload that knows nothing about BaseLanguage.
        modelBuilder.ApplyMultiLingual();
    }
}

public sealed class BaseLanguageMisconfigurationTests
{
    [Fact]
    public async Task AMappingThatDisagreesWithTheEngine_FailsLoudly()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddMultiLingual(o => o.BaseLanguage = "en").UseEntityFrameworkCore<MismatchedDbContext>().Build();
        services.AddDbContext<MismatchedDbContext>((sp, o) => o.UseSqlite(connection).UseMultiLingual(sp));

        await using var root  = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = root.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<MismatchedDbContext>();
        await db.Database.EnsureCreatedAsync();
        await scope.ServiceProvider.GetRequiredService<ILanguageManager>()
            .CreateAsync(new NewLanguage { Code = "en", Name = "English" });

        db.Products.Add(new ShopProduct { Sku = "X", Price = 1, Name = "Laptop" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("BaseLanguage", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ApplyMultiLingual", ex.Message, StringComparison.Ordinal);
    }
}
