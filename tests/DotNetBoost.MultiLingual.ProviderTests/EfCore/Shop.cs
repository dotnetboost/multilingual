using DotNetBoost.MultiLingual.Core.Attributes;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

public class ShopProduct
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public decimal Price { get; set; }

    [MultiLingual] public string? Name { get; set; }
    [MultiLingual] public string? Description { get; set; }

    public int? CategoryId { get; set; }
    public ShopCategory? Category { get; set; }
}

[MultiLingual]
public class ShopCategory
{
    public int Id { get; set; }
    public string? Name { get; set; }
    [NotMultiLingual] public string Slug { get; set; } = string.Empty;
    public ICollection<ShopProduct> Products { get; set; } = [];
}

[MultiLingualEntity("document")]
public class ShopDocument
{
    public Guid Id { get; set; }
    [MultiLingual] public string? Title { get; set; }
}

public sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options), IMultiLingualDbContext
{
    public DbSet<Translation> Translations => Set<Translation>();
    public DbSet<Language> Languages => Set<Language>();
    public DbSet<ShopProduct> Products => Set<ShopProduct>();
    public DbSet<ShopCategory> Categories => Set<ShopCategory>();
    public DbSet<ShopDocument> Documents => Set<ShopDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShopProduct>().Property(p => p.Price).HasConversion<double>();
        modelBuilder.Entity<ShopDocument>().Property(d => d.Id).ValueGeneratedNever();
        modelBuilder.ApplyMultiLingual();
    }
}

/// <summary>
/// The EF Core provider wired exactly as an application wires it — AddMultiLingual,
/// UseEntityFrameworkCore, AddDbContext((sp, o) =&gt; o.UseMultiLingual(sp)) — over a private
/// in-memory SQLite database seeded with en (default), ar and fr.
/// </summary>
internal sealed class Shop : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _root;

    private Shop(SqliteConnection connection, ServiceProvider root)
    {
        _connection = connection;
        _root       = root;
    }

    /// <param name="configureSqlite">Provider options, such as an execution strategy.</param>
    /// <param name="interceptors">Extra interceptors for every context the shop creates.</param>
    public static Task<Shop> StartAsync(
        Action<SqliteDbContextOptionsBuilder>? configureSqlite = null,
        params IInterceptor[] interceptors) =>
        StartCoreAsync(configureSqlite, configureServices: null, interceptors);

    /// <summary>A shop whose registrations <paramref name="configureServices"/> may replace.</summary>
    public static Task<Shop> StartWithServicesAsync(Action<IServiceCollection> configureServices) =>
        StartCoreAsync(configureSqlite: null, configureServices, []);

    private static async Task<Shop> StartCoreAsync(
        Action<SqliteDbContextOptionsBuilder>? configureSqlite,
        Action<IServiceCollection>? configureServices,
        IInterceptor[] interceptors)
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        await connection.OpenAsync();

        var services = new ServiceCollection();

        // Loggers without an ILoggerFactory: EF would otherwise pick up each test's own factory
        // from the application container and build a separate internal provider per test.
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddMultiLingual().UseEntityFrameworkCore<ShopDbContext>().Build();
        services.AddDbContext<ShopDbContext>((sp, options) => options
            .UseSqlite(connection, sqlite => configureSqlite?.Invoke(sqlite))
            .UseMultiLingual(sp)
            .AddInterceptors(interceptors));

        configureServices?.Invoke(services);

        var shop = new Shop(connection, services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));

        await using var scope = shop.Scope();
        await scope.Db.Database.EnsureCreatedAsync();
        var languages = scope.Get<ILanguageManager>();
        await languages.CreateAsync(new NewLanguage { Code = "en", Name = "English" });
        await languages.CreateAsync(new NewLanguage { Code = "ar", Name = "Arabic" });
        await languages.CreateAsync(new NewLanguage { Code = "fr", Name = "French" });

        return shop;
    }

    public ShopScope Scope() => new(_root.CreateAsyncScope());

    /// <summary>A scope with its language resolved, as the request middleware leaves it.</summary>
    public async Task<ShopScope> ScopeInAsync(string languageCode)
    {
        var scope = Scope();
        await scope.Get<ILanguageContext>().UseAsync(languageCode);
        return scope;
    }

    /// <summary>What is actually stored, read around the MultiLingual machinery.</summary>
    public async Task<List<Translation>> StoredTranslationsAsync()
    {
        await using var scope = Scope();
        return await scope.Db.Translations.AsNoTracking()
            .OrderBy(t => t.EntityName).ThenBy(t => t.EntityId).ThenBy(t => t.PropertyName).ThenBy(t => t.LanguageCode)
            .ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

internal sealed class ShopScope(AsyncServiceScope scope) : IAsyncDisposable
{
    public T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public ShopDbContext Db => Get<ShopDbContext>();

    public ITranslationHydrator Hydrator => Get<ITranslationHydrator>();

    public ITranslationManager Translations => Get<ITranslationManager>();

    public IMultiLingualManager MultiLingual => Get<IMultiLingualManager>();

    public ValueTask DisposeAsync() => scope.DisposeAsync();
}
