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
/// <c>SetAll</c> and <c>GetAllAsync(includeBase: true)</c> in base-language mode on real engines:
/// the base columns are read with a query of their own, by key, around the translation machinery.
/// </summary>
public abstract class AllLanguagesIntegrationTests : IAsyncLifetime
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
        services.AddMultiLingual(o => o.BaseLanguage = "en").UseEntityFrameworkCore<BaseShopDbContext>().Build();
        services.AddDbContext<BaseShopDbContext>((sp, options) =>
        {
            UseProvider(options, connectionString);
            options.UseMultiLingual(sp);
        });

        _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using var scope = _root.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BaseShopDbContext>().Database.EnsureCreatedAsync();

        var languages = scope.ServiceProvider.GetRequiredService<ILanguageManager>();
        await languages.CreateAsync(new NewLanguage { Code = "en", Name = "English" });
        await languages.CreateAsync(new NewLanguage { Code = "ar", Name = "Arabic" });
    }

    public async Task DisposeAsync() => await _root.DisposeAsync();

    private async Task<AsyncServiceScope> ScopeInAsync(string languageCode)
    {
        var scope = _root.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILanguageContext>().UseAsync(languageCode);
        return scope;
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string?>> Names(params (string Code, string Name)[] names) =>
        names.ToDictionary(n => n.Code, n => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["Name"] = n.Name });

    [Fact]
    public async Task AnEditorRoundTrip_UnderAnotherLanguage_WritesAndReadsEveryLanguage()
    {
        int id;
        await using (var scope = await ScopeInAsync("ar"))
        {
            var db      = scope.ServiceProvider.GetRequiredService<BaseShopDbContext>();
            var product = new ShopProduct { Sku = "LAPTOP" };
            scope.ServiceProvider.GetRequiredService<IMultiLingualManager>().For(product).SetAll(Names(("en", "Laptop"), ("ar", "حاسوب")));
            db.Products.Add(product);
            await db.SaveChangesAsync();
            id = product.Id;
        }

        await using (var scope = await ScopeInAsync("ar"))
        {
            var db      = scope.ServiceProvider.GetRequiredService<BaseShopDbContext>();
            var product = await db.Products.SingleAsync(p => p.Id == id);
            Assert.Equal("حاسوب", product.Name);

            var all = await scope.ServiceProvider.GetRequiredService<IMultiLingualManager>().For(product).GetAllAsync(includeBase: true);
            Assert.Equal("Laptop", all["en"]["Name"]);
            Assert.Equal("حاسوب", all["ar"]["Name"]);

            var stored = await db.Products.AsNoTracking().IgnoreTranslations().SingleAsync(p => p.Id == id);
            Assert.Equal("Laptop", stored.Name);
            Assert.Empty(await db.Translations.Where(t => t.LanguageCode == "en").ToListAsync());
        }
    }
}

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresAllLanguagesTests(PostgreSqlFixture fixture) : AllLanguagesIntegrationTests
{
    protected override Task<string> CreateDatabaseAsync() => fixture.CreateDatabaseAsync();

    protected override void UseProvider(DbContextOptionsBuilder options, string connectionString) => options.UseNpgsql(connectionString);
}

[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerAllLanguagesTests(SqlServerFixture fixture) : AllLanguagesIntegrationTests
{
    protected override Task<string> CreateDatabaseAsync() => fixture.CreateDatabaseAsync();

    protected override void UseProvider(DbContextOptionsBuilder options, string connectionString) => options.UseSqlServer(connectionString);
}
