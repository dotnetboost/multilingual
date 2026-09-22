using DotNetBoost.MultiLingual.Core.Attributes;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

// A model for the less common EF Core features: automatic includes, an inheritance hierarchy and
// lazy-loading proxies — which is why the entities are public, unsealed, with virtual navigations.

[MultiLingual]
public class EdgeCategory
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public virtual ICollection<EdgeProduct> Products { get; set; } = [];
}

public class EdgeProduct
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public int Price { get; set; }
    [MultiLingual] public string? Name { get; set; }
    public int? CategoryId { get; set; }
    public virtual EdgeCategory? Category { get; set; }
}

/// <summary>The base of a TPH hierarchy; every type in it shares the entity name "article".</summary>
[MultiLingualEntity("article")]
public class EdgeArticle
{
    public int Id { get; set; }
    [MultiLingual] public string? Title { get; set; }
}

/// <summary>A derived type with a translatable property of its own.</summary>
public class EdgeNews : EdgeArticle
{
    [MultiLingual] public string? Summary { get; set; }
}

public abstract class EdgeDbContextBase(DbContextOptions options) : DbContext(options), IMultiLingualDbContext
{
    public DbSet<Translation> Translations => Set<Translation>();
    public DbSet<Language> Languages => Set<Language>();
    public DbSet<EdgeProduct> Products => Set<EdgeProduct>();
    public DbSet<EdgeCategory> Categories => Set<EdgeCategory>();
    public DbSet<EdgeArticle> Articles => Set<EdgeArticle>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EdgeNews>();
        Configure(modelBuilder);
        modelBuilder.ApplyMultiLingual();
    }

    protected virtual void Configure(ModelBuilder modelBuilder) { }
}

/// <summary>Products always come with their category: an automatic include.</summary>
public sealed class EdgeDbContext(DbContextOptions<EdgeDbContext> options) : EdgeDbContextBase(options)
{
    protected override void Configure(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<EdgeProduct>().Navigation(p => p.Category).AutoInclude();
}

/// <summary>The same model with lazy-loading proxies, and no automatic include to hide them.</summary>
public sealed class LazyEdgeDbContext(DbContextOptions<LazyEdgeDbContext> options) : EdgeDbContextBase(options);

/// <summary>The EF Core provider over <typeparamref name="TContext"/>, wired as an application wires it.</summary>
internal sealed class EdgeShop<TContext> : IAsyncDisposable where TContext : EdgeDbContextBase
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _root;

    private EdgeShop(SqliteConnection connection, ServiceProvider root)
    {
        _connection = connection;
        _root       = root;
    }

    public static async Task<EdgeShop<TContext>> StartAsync(Action<DbContextOptionsBuilder>? configure = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddMultiLingual().UseEntityFrameworkCore<TContext>().Build();
        services.AddDbContext<TContext>((sp, options) =>
        {
            options.UseSqlite(connection).UseMultiLingual(sp);
            configure?.Invoke(options);
        });

        var shop = new EdgeShop<TContext>(connection, services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));

        await using var scope = shop.Scope();
        await scope.Db.Database.EnsureCreatedAsync();
        await scope.MultiLingual.Languages.CreateAsync(new NewLanguage { Code = "en", Name = "English" });
        await scope.MultiLingual.Languages.CreateAsync(new NewLanguage { Code = "fr", Name = "French" });

        return shop;
    }

    public EdgeScope<TContext> Scope() => new(_root.CreateAsyncScope());

    public async Task<EdgeScope<TContext>> ScopeInAsync(string languageCode)
    {
        var scope = Scope();
        await scope.MultiLingual.UseLanguageAsync(languageCode);
        return scope;
    }

    public async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

internal sealed class EdgeScope<TContext>(AsyncServiceScope scope) : IAsyncDisposable where TContext : EdgeDbContextBase
{
    public TContext Db => scope.ServiceProvider.GetRequiredService<TContext>();

    public IMultiLingualManager MultiLingual => scope.ServiceProvider.GetRequiredService<IMultiLingualManager>();

    public ValueTask DisposeAsync() => scope.DisposeAsync();
}
