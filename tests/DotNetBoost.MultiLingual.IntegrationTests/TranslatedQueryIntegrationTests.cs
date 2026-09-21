using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using DotNetBoost.MultiLingual.ProviderTests.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.MultiLingual.IntegrationTests;

/// <summary>
/// Translated filtering and sorting, and clearing a translation, on real engines — where the
/// SQL differs: how a key is cast to text (a Guid's case above all), and how a correlated
/// subquery inside COALESCE and ORDER BY is written.
/// </summary>
public abstract class TranslatedQueryIntegrationTests : IAsyncLifetime
{
    private ServiceProvider _root = null!;

    /// <summary>A fresh database's connection string.</summary>
    protected abstract Task<string> CreateDatabaseAsync();

    protected abstract void UseProvider(DbContextOptionsBuilder options, string connectionString);

    public async Task InitializeAsync()
    {
        var connectionString = await CreateDatabaseAsync();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddMultiLingual().UseEntityFrameworkCore<ShopDbContext>().Build();
        services.AddDbContext<ShopDbContext>((sp, options) =>
        {
            UseProvider(options, connectionString);
            options.UseMultiLingual(sp);
        });

        _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using var scope = _root.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Database.EnsureCreatedAsync();

        var languages = scope.ServiceProvider.GetRequiredService<ILanguageManager>();
        await languages.CreateAsync(new NewLanguage { Code = "en", Name = "English" });
        await languages.CreateAsync(new NewLanguage { Code = "fr", Name = "French" });
    }

    public async Task DisposeAsync() => await _root.DisposeAsync();

    private async Task<AsyncServiceScope> ScopeInAsync(string languageCode)
    {
        var scope = _root.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILanguageContext>().UseAsync(languageCode);
        return scope;
    }

    private static Dictionary<string, string?> Values(string key, string value) => new() { [key] = value };

    private async Task SeedAsync()
    {
        await using var scope = _root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        var translations = scope.ServiceProvider.GetRequiredService<ITranslationManager>();

        void Add(string sku, string en, string? fr)
        {
            var product = new ShopProduct { Sku = sku };
            translations.Stage(product, "en", Values("Name", en));
            if (fr is not null) translations.Stage(product, "fr", Values("Name", fr));
            db.Products.Add(product);
        }

        Add("LAPTOP", "Laptop", "Portable");
        Add("MOUSE", "Mouse", null);
        Add("KEYBOARD", "Keyboard", "Clavier");
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task IntKeys_FilterAndSort_WithFallback()
    {
        await SeedAsync();
        await using var scope = await ScopeInAsync("fr");
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();

        var sorted = await db.Products.OrderByTranslation(db, p => p.Name).Select(p => p.Sku).ToListAsync();
        Assert.Equal(["KEYBOARD", "MOUSE", "LAPTOP"], sorted);   // Clavier, Mouse (fallback), Portable

        var mouse = await db.Products.WhereTranslation(db, p => p.Name, n => n == "Mouse").Select(p => p.Sku).ToListAsync();
        Assert.Equal(["MOUSE"], mouse);
    }

    [Fact]
    public async Task GuidKeys_Filter()
    {
        var id = Guid.NewGuid();
        await using (var write = await ScopeInAsync("fr"))
        {
            var db = write.ServiceProvider.GetRequiredService<ShopDbContext>();
            db.Documents.Add(new ShopDocument { Id = id, Title = "Guide" });
            db.Documents.Add(new ShopDocument { Id = Guid.NewGuid(), Title = "Livre" });
            await db.SaveChangesAsync();
        }

        await using var scope = await ScopeInAsync("fr");
        var read = scope.ServiceProvider.GetRequiredService<ShopDbContext>();

        var ids = await read.Documents.WhereTranslation(read, d => d.Title, t => t == "Guide").Select(d => d.Id).ToListAsync();
        Assert.Equal([id], ids);
    }

    [Fact]
    public async Task SelectTranslated_ThroughANavigation_AndANestedCollection()
    {
        await using (var seed = _root.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ShopDbContext>();
            var translations = seed.ServiceProvider.GetRequiredService<ITranslationManager>();

            var books = new ShopCategory { Slug = "books" };
            translations.Stage(books, "en", Values("Name", "Books"));
            translations.Stage(books, "fr", Values("Name", "Livres"));

            var laptop = new ShopProduct { Sku = "LAPTOP", Category = books };
            translations.Stage(laptop, "en", Values("Name", "Laptop"));

            db.Products.Add(laptop);
            await db.SaveChangesAsync();
        }

        await using var scope = await ScopeInAsync("fr");
        var read = scope.ServiceProvider.GetRequiredService<ShopDbContext>();

        var product = await read.Products
            .SelectTranslated(read, p => new { p.Sku, p.Name, Category = p.Category!.Name })
            .SingleAsync();
        Assert.Equal(("LAPTOP", "Laptop", "Livres"), (product.Sku, product.Name, product.Category));   // Name falls back to en

        var category = await read.Categories
            .SelectTranslated(read, c => new { c.Name, Products = c.Products.Select(p => p.Name).ToList() })
            .SingleAsync();
        Assert.Equal("Livres", category.Name);
        Assert.Equal(["Laptop"], category.Products);
    }

    [Fact]
    public async Task PurgeOrphans_RemovesOnlyRowsWhoseEntityIsGone_IntAndGuidKeys()
    {
        await SeedAsync();
        var keptDocument = Guid.NewGuid();
        var goneDocument = Guid.NewGuid();

        await using (var write = await ScopeInAsync("fr"))
        {
            var db = write.ServiceProvider.GetRequiredService<ShopDbContext>();
            db.Documents.AddRange(new ShopDocument { Id = keptDocument, Title = "Gardé" }, new ShopDocument { Id = goneDocument, Title = "Parti" });
            await db.SaveChangesAsync();
        }

        await using var scope = _root.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ShopDbContext>();

        // Nothing is orphaned yet: every live row — int and Guid keyed — must survive the match.
        Assert.Equal(0, await context.PurgeOrphanedTranslationsAsync());

        await context.Products.Where(p => p.Sku == "LAPTOP").ExecuteDeleteAsync();   // en + fr rows
        await context.Documents.Where(d => d.Id == goneDocument).ExecuteDeleteAsync(); // fr row

        Assert.Equal(3, await context.PurgeOrphanedTranslationsAsync());

        var remaining = await context.Translations.AsNoTracking().Select(t => t.Value).ToListAsync();
        Assert.Equal(["Clavier", "Gardé", "Keyboard", "Mouse"], remaining.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Clear_RemovesTheRow_ThroughSaveChanges()
    {
        await SeedAsync();
        await using var scope = await ScopeInAsync("fr");
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();

        var laptop = await db.Products.SingleAsync(p => p.Sku == "LAPTOP");
        scope.ServiceProvider.GetRequiredService<ITranslationManager>().Clear(laptop, "fr");
        await db.SaveChangesAsync();

        var key = laptop.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(await db.Translations.AnyAsync(t => t.LanguageCode == "fr" && t.EntityId == key));
        Assert.Equal(["LAPTOP"], await db.Products.WhereTranslation(db, p => p.Name, n => n == "Laptop").Select(p => p.Sku).ToListAsync());
    }
}

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresTranslatedQueryTests(PostgreSqlFixture fixture) : TranslatedQueryIntegrationTests
{
    protected override Task<string> CreateDatabaseAsync() => fixture.CreateDatabaseAsync();

    protected override void UseProvider(DbContextOptionsBuilder options, string connectionString) => options.UseNpgsql(connectionString);
}

[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerTranslatedQueryTests(SqlServerFixture fixture) : TranslatedQueryIntegrationTests
{
    protected override Task<string> CreateDatabaseAsync() => fixture.CreateDatabaseAsync();

    protected override void UseProvider(DbContextOptionsBuilder options, string connectionString) => options.UseSqlServer(connectionString);
}

/// <summary>
/// The same suite on a case-sensitive SQL Server database. The default collation compares key
/// text case-insensitively and so would hide a key compared in the wrong case — which for the
/// orphan purge means deleting live rows. SQL Server casts a Guid to upper-case text; the stored
/// form is lower case.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerCaseSensitiveTranslatedQueryTests(SqlServerFixture fixture) : TranslatedQueryIntegrationTests
{
    protected override Task<string> CreateDatabaseAsync() => fixture.CreateDatabaseAsync("Latin1_General_100_CS_AS");

    protected override void UseProvider(DbContextOptionsBuilder options, string connectionString) => options.UseSqlServer(connectionString);
}
