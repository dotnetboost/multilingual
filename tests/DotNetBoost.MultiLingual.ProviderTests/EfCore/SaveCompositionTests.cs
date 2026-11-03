using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.MultiLingual.ProviderTests.EfCore;

/// <summary>A row the follow-up interceptor writes. Nothing translatable about it.</summary>
public class FollowUpNote
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>Products, plus the follow-up interceptor's notes. The mapping follows the engine's configuration.</summary>
public abstract class ComposedDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Translation> Translations => Set<Translation>();
    public DbSet<ShopProduct> Products => Set<ShopProduct>();
    public DbSet<FollowUpNote> Notes => Set<FollowUpNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShopProduct>().Property(p => p.Price).HasConversion<double>();
        modelBuilder.Ignore<ShopDocument>();
        modelBuilder.ApplyMultiLingual(this);
    }
}

// One type per mode: EF caches a model per context type, and the two modes map differently.
public sealed class ComposedShopDbContext(DbContextOptions<ComposedShopDbContext> options) : ComposedDbContext(options);

public sealed class BaseComposedShopDbContext(DbContextOptions<BaseComposedShopDbContext> options) : ComposedDbContext(options);

/// <summary>Where the follow-up interceptor sits relative to <c>UseMultiLingual</c>.</summary>
public enum FollowUpOrder
{
    Before,
    After
}

public sealed class FollowUpFailedException() : Exception("Simulated failure after the follow-up save.");

/// <summary>What the follow-up interceptor saw, and what it is told to do.</summary>
internal sealed class FollowUpProbe
{
    public bool Enabled { get; set; } = true;

    public bool FailAfterFollowUp { get; set; }

    /// <summary>One entry per follow-up: whether a transaction was still open when it ran.</summary>
    public List<bool> RanInsideTransaction { get; } = [];
}

/// <summary>
/// Another library's post-save work, shaped like Auditing's deferred pass: it notes what the
/// outermost save changed, and once that save completes it adds a row of its own and saves again,
/// on the same context, relying on whatever transaction is open. Nested saves, its own or anyone
/// else's, neither start nor repeat it.
/// </summary>
internal sealed class FollowUpSaveInterceptor(FollowUpProbe probe) : SaveChangesInterceptor
{
    private int _depth;
    private bool _captured;
    private bool _following;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!_following && eventData.Context is { } context && _depth++ == 0)
        {
            _captured = context.ChangeTracker.Entries<ShopProduct>()
                .Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        }

        return ValueTask.FromResult(result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (_following || eventData.Context is not { } context || --_depth > 0 || !_captured)
            return result;

        _captured = false;
        if (!probe.Enabled)
            return result;

        _following = true;
        try
        {
            probe.RanInsideTransaction.Add(context.Database.CurrentTransaction is not null);
            context.Add(new FollowUpNote { Text = "follow-up" });
            await context.SaveChangesAsync(cancellationToken);

            if (probe.FailAfterFollowUp)
                throw new FollowUpFailedException();
        }
        finally
        {
            _following = false;
        }

        return result;
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (!_following && _depth > 0 && --_depth == 0)
            _captured = false;

        return Task.CompletedTask;
    }
}

/// <summary>
/// The EF Core provider beside <see cref="FollowUpSaveInterceptor"/>, over a private in-memory
/// SQLite database seeded with en (default), ar and fr.
/// </summary>
internal sealed class ComposedShop : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _root;
    private readonly Type _contextType;
    private readonly bool _baseLanguage;

    private ComposedShop(SqliteConnection connection, ServiceProvider root, Type contextType, bool baseLanguage, FollowUpProbe probe)
    {
        _connection   = connection;
        _root         = root;
        _contextType  = contextType;
        _baseLanguage = baseLanguage;
        Probe         = probe;
    }

    public FollowUpProbe Probe { get; }

    /// <param name="order">Where the follow-up interceptor is registered.</param>
    /// <param name="retrying">Whether a retrying execution strategy is configured.</param>
    /// <param name="baseLanguage">Whether <c>BaseLanguage = "en"</c>.</param>
    public static async Task<ComposedShop> StartAsync(FollowUpOrder order, bool retrying, bool baseLanguage = false)
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        await connection.OpenAsync();

        var probe    = new FollowUpProbe();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var contextType = baseLanguage
            ? Register<BaseComposedShopDbContext>(services, connection, order, retrying, probe, o => o.BaseLanguage = "en")
            : Register<ComposedShopDbContext>(services, connection, order, retrying, probe, _ => { });

        var shop = new ComposedShop(
            connection, services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), contextType, baseLanguage, probe);

        await using var scope = shop.Scope();
        await scope.Db.Database.EnsureCreatedAsync();
        var languages = scope.Get<ILanguageManager>();
        await languages.CreateAsync(new NewLanguage { Code = "en", Name = "English" });
        await languages.CreateAsync(new NewLanguage { Code = "ar", Name = "Arabic" });
        await languages.CreateAsync(new NewLanguage { Code = "fr", Name = "French" });

        return shop;
    }

    private static Type Register<TContext>(
        IServiceCollection services,
        SqliteConnection connection,
        FollowUpOrder order,
        bool retrying,
        FollowUpProbe probe,
        Action<Core.MultiLingualOptions> configure)
        where TContext : ComposedDbContext
    {
        services.AddMultiLingual(configure).UseEntityFrameworkCore<TContext>().Build();
        services.AddDbContext<TContext>((sp, options) =>
        {
            // The cached delegate, so every test's options look alike to EF's internal provider cache.
            options.UseSqlite(connection, retrying ? TestRetryingStrategy.Configure : null);

            if (order == FollowUpOrder.Before)
                options.AddInterceptors(new FollowUpSaveInterceptor(probe));

            options.UseMultiLingual(sp);

            if (order == FollowUpOrder.After)
                options.AddInterceptors(new FollowUpSaveInterceptor(probe));
        });

        return typeof(TContext);
    }

    public ComposedScope Scope() => new(_root.CreateAsyncScope(), _contextType);

    /// <summary>A scope with its language resolved, as the request middleware leaves it.</summary>
    public async Task<ComposedScope> ScopeInAsync(string languageCode)
    {
        var scope = Scope();
        await scope.Get<ILanguageContext>().UseAsync(languageCode);
        return scope;
    }

    /// <summary>
    /// A product named <paramref name="name"/> in English, plus <paramref name="translations"/>,
    /// written without the follow-up running.
    /// </summary>
    public async Task<int> SeedProductAsync(string name, params (string Language, string Name)[] translations)
    {
        Probe.Enabled = false;
        try
        {
            await using var scope = Scope();

            // No language resolved: in base-language mode Name goes to the column; otherwise only
            // staged values are written, so English is staged as well.
            var product = new ShopProduct { Sku = "LAPTOP", Price = 10, Name = name };
            if (!_baseLanguage)
                scope.Translations.Stage(product, "en", new Dictionary<string, string?> { ["Name"] = name });

            foreach (var (language, value) in translations)
                scope.Translations.Stage(product, language, new Dictionary<string, string?> { ["Name"] = value });

            scope.Db.Products.Add(product);
            await scope.Db.SaveChangesAsync();
            return product.Id;
        }
        finally
        {
            Probe.Enabled = true;
        }
    }

    /// <summary>Every stored product name, as (language, value), read around the MultiLingual machinery.</summary>
    public async Task<List<(string Language, string? Value)>> StoredNamesAsync()
    {
        await using var scope = Scope();
        var rows = await scope.Db.Translations.AsNoTracking()
            .Where(t => t.EntityName == "ShopProduct" && t.PropertyName == "Name")
            .OrderBy(t => t.LanguageCode)
            .ToListAsync();

        return rows.Select(t => (t.LanguageCode, t.Value)).ToList();
    }

    /// <summary>The raw <c>Products.Name</c> column (base-language mode only).</summary>
    public async Task<string?> StoredColumnNameAsync(int productId)
    {
        await using var scope = Scope();
        var product = await scope.Db.Products.AsNoTracking().IgnoreTranslations().SingleAsync(p => p.Id == productId);
        return product.Name;
    }

    public async Task<(int Products, int Translations, int Notes)> StoredCountsAsync()
    {
        await using var scope = Scope();
        return (await scope.Db.Products.IgnoreTranslations().CountAsync(),
                await scope.Db.Translations.CountAsync(),
                await scope.Db.Notes.CountAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

internal sealed class ComposedScope(AsyncServiceScope scope, Type contextType) : IAsyncDisposable
{
    public T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public ComposedDbContext Db => (ComposedDbContext)scope.ServiceProvider.GetRequiredService(contextType);

    public ITranslationManager Translations => Get<ITranslationManager>();

    public IMultiLingualManager MultiLingual => Get<IMultiLingualManager>();

    public ValueTask DisposeAsync() => scope.DisposeAsync();
}

/// <summary>
/// The save interceptor beside another interceptor that saves again on the same context once a
/// save completes — the shape of Auditing's deferred pass. See <c>COMPOSITION-GAPS.md</c>: each
/// case runs with that interceptor registered before and after <c>UseMultiLingual</c>, with and
/// without a retrying strategy. A row that fails today is skipped with the gap it belongs to.
/// </summary>
public sealed class SaveCompositionTests
{
    private const string Gap1 = "COMPOSITION-GAPS.md gap 1: a save nested inside ours resets the outer save's state.";
    private const string Gap2 = "COMPOSITION-GAPS.md gap 2: the owned transaction is committed before later interceptors run.";
    private const string Gap2Retrying =
        "COMPOSITION-GAPS.md gap 2: under a retrying strategy, an interceptor registered first completes after the strategy's commit.";

    // ----------------------------------------------------------------- transaction

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false, Skip = Gap2)]
    [InlineData(FollowUpOrder.Before, true, Skip = Gap2Retrying)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task Create_RunsTheFollowUpInsideTheTransaction(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying);

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Price = 1, Name = "Souris" });
            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal([true], shop.Probe.RanInsideTransaction);
    }

    [Theory]
    [InlineData(FollowUpOrder.Before, false, Skip = Gap1)]
    [InlineData(FollowUpOrder.After, false, Skip = Gap2)]
    [InlineData(FollowUpOrder.Before, true, Skip = Gap2Retrying)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task FailureAfterTheFollowUp_RollsBackEverything(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying);
        shop.Probe.FailAfterFollowUp = true;

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Price = 1, Name = "Souris" });
            await Assert.ThrowsAsync<FollowUpFailedException>(() => scope.Db.SaveChangesAsync());
        }

        Assert.Equal((0, 0, 0), await shop.StoredCountsAsync());
    }

    // ----------------------------------------------------------------- content

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task Create_WritesTheTranslation_AndTheFollowUpRow(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying);

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Price = 1, Name = "Souris" });
            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal([("fr", (string?)"Souris")], await shop.StoredNamesAsync());
        Assert.Equal((1, 1, 1), await shop.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before, false, Skip = Gap1)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task Delete_RemovesTheTranslations(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying);
        var id = await shop.SeedProductAsync("Laptop", ("fr", "Ordinateur"));

        await using (var scope = shop.Scope())
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            scope.Db.Products.Remove(product);
            await scope.Db.SaveChangesAsync();
        }

        Assert.Empty(await shop.StoredNamesAsync());
        Assert.Equal((0, 0, 1), await shop.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task Update_AfterAFallbackRead_LeavesTheStoredTextAlone(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying);
        var id = await shop.SeedProductAsync("Laptop", ("fr", "Ordinateur"));

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            Assert.Equal("Laptop", product.Name);   // the English fallback

            product.Price = 20;
            await scope.Db.SaveChangesAsync();
        }

        // No translation work, so no transaction of ours: the follow-up runs, but in whatever
        // transaction its own caller provides. Only the retrying path opens one regardless.
        Assert.Equal([("en", (string?)"Laptop"), ("fr", "Ordinateur")], await shop.StoredNamesAsync());
        Assert.Single(shop.Probe.RanInsideTransaction);
    }

    // ----------------------------------------------------------------- base language (gap 1's empty name)

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task BaseLanguage_UpdateUnderAnotherLanguage_KeepsTheColumn(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying, baseLanguage: true);
        var id = await shop.SeedProductAsync("Laptop", ("ar", "حاسوب"));

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            Assert.Equal("حاسوب", product.Name);

            product.Price = 20;
            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal("Laptop", await shop.StoredColumnNameAsync(id));
        Assert.Equal([("ar", (string?)"حاسوب")], await shop.StoredNamesAsync());
        Assert.Single(shop.Probe.RanInsideTransaction);
    }

    /// <summary>The sample's <c>TranslatedText.SaveAsync</c>: switch to the base language, assign, set the rest, save, switch back.</summary>
    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task BaseLanguage_TheSamplesEditorFlow_KeepsBothLanguages(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying, baseLanguage: true);
        var id = await shop.SeedProductAsync("Laptop", ("ar", "حاسوب"));

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            await scope.MultiLingual.UseLanguageAsync("en");
            try
            {
                var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
                product.Name  = "Laptop Pro";
                product.Price = 20;
                scope.MultiLingual.For(product).Set("ar", new Dictionary<string, string?> { ["Name"] = "حاسوب برو" });

                await scope.Db.SaveChangesAsync();
            }
            finally
            {
                await scope.MultiLingual.UseLanguageAsync("ar");
            }
        }

        Assert.Equal("Laptop Pro", await shop.StoredColumnNameAsync(id));
        Assert.Equal([("ar", (string?)"حاسوب برو")], await shop.StoredNamesAsync());
        Assert.Single(shop.Probe.RanInsideTransaction);
    }

    // ----------------------------------------------------------------- control: the caller's transaction

    [Theory]
    [InlineData(FollowUpOrder.Before)]
    [InlineData(FollowUpOrder.After)]
    public async Task CallerTransaction_KeepsEverythingAtomic(FollowUpOrder order)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying: false);
        shop.Probe.FailAfterFollowUp = true;

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            await using var transaction = await scope.Db.Database.BeginTransactionAsync();
            scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Price = 1, Name = "Souris" });
            await Assert.ThrowsAsync<FollowUpFailedException>(() => scope.Db.SaveChangesAsync());
        }

        Assert.Equal([true], shop.Probe.RanInsideTransaction);
        Assert.Equal((0, 0, 0), await shop.StoredCountsAsync());
    }
}
