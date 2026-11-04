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

/// <summary>
/// The application-owned transaction <c>docs/limitations.md</c> recommends beside other
/// <c>SaveChanges</c> interceptors when no retrying strategy is configured.
/// </summary>
public sealed class AppTransactionDbContext(DbContextOptions<AppTransactionDbContext> options) : ComposedDbContext(options)
{
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
            return base.SaveChanges(acceptAllChangesOnSuccess);

        using var transaction = Database.BeginTransaction();
        var saved = base.SaveChanges(acceptAllChangesOnSuccess);
        transaction.Commit();
        return saved;
    }
}

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

    /// <summary>Makes the follow-up save itself fail, on a NOT NULL column.</summary>
    public bool FailTheFollowUpSave { get; set; }

    /// <summary>Makes the follow-up save add a translatable product too, named in the scope's language.</summary>
    public bool AddProductInFollowUp { get; set; }

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
        Starting(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Starting(eventData.Context);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (Completing(eventData.Context) is not { } context)
            return result;

        _following = true;
        try
        {
            AddNote(context);
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

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (Completing(eventData.Context) is not { } context)
            return result;

        _following = true;
        try
        {
            AddNote(context);
            context.SaveChanges();

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
        Failed();
        return Task.CompletedTask;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Failed();

    private void Starting(DbContext? context)
    {
        if (!_following && context is not null && _depth++ == 0)
        {
            _captured = context.ChangeTracker.Entries<ShopProduct>()
                .Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        }
    }

    /// <summary>The context to follow up on, when the outermost save that changed a product just completed.</summary>
    private DbContext? Completing(DbContext? context)
    {
        if (_following || context is null || --_depth > 0 || !_captured)
            return null;

        _captured = false;
        return probe.Enabled ? context : null;
    }

    private void AddNote(DbContext context)
    {
        probe.RanInsideTransaction.Add(context.Database.CurrentTransaction is not null);
        context.Add(new FollowUpNote { Text = probe.FailTheFollowUpSave ? null! : "follow-up" });

        if (probe.AddProductInFollowUp)
            context.Add(new ShopProduct { Sku = "NOTE", Name = "Note" });
    }

    private void Failed()
    {
        if (!_following && _depth > 0 && --_depth == 0)
            _captured = false;
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
    private readonly bool _ownTransactions;

    private ComposedShop(
        SqliteConnection connection, ServiceProvider root, Type contextType, bool baseLanguage, bool ownTransactions, FollowUpProbe probe)
    {
        _connection      = connection;
        _root            = root;
        _contextType     = contextType;
        _baseLanguage    = baseLanguage;
        _ownTransactions = ownTransactions;
        Probe            = probe;
    }

    public FollowUpProbe Probe { get; }

    /// <param name="order">Where the follow-up interceptor is registered.</param>
    /// <param name="retrying">Whether a retrying execution strategy is configured.</param>
    /// <param name="baseLanguage">Whether <c>BaseLanguage = "en"</c>.</param>
    /// <param name="ownTransactions">What <c>OwnTransactions(bool)</c> is set to.</param>
    /// <param name="appTransactions">Whether the context opens its own transaction around every save.</param>
    /// <param name="extra">Further interceptors, added last.</param>
    public static async Task<ComposedShop> StartAsync(
        FollowUpOrder order,
        bool retrying,
        bool baseLanguage = false,
        bool ownTransactions = true,
        bool appTransactions = false,
        IInterceptor[]? extra = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        await connection.OpenAsync();

        var probe    = new FollowUpProbe();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var contextType = (baseLanguage, appTransactions) switch
        {
            (true, false) => Register<BaseComposedShopDbContext>(services, connection, order, retrying, ownTransactions, probe, extra, o => o.BaseLanguage = "en"),
            (false, false) => Register<ComposedShopDbContext>(services, connection, order, retrying, ownTransactions, probe, extra, _ => { }),
            (false, true) => Register<AppTransactionDbContext>(services, connection, order, retrying, ownTransactions, probe, extra, _ => { }),
            _ => throw new NotSupportedException("No base-language context with application transactions."),
        };

        var shop = new ComposedShop(
            connection, services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), contextType, baseLanguage, ownTransactions, probe);

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
        bool ownTransactions,
        FollowUpProbe probe,
        IInterceptor[]? extra,
        Action<Core.MultiLingualOptions> configure)
        where TContext : ComposedDbContext
    {
        services.AddMultiLingual(configure).UseEntityFrameworkCore<TContext>().OwnTransactions(ownTransactions).Build();
        services.AddDbContext<TContext>((sp, options) =>
        {
            // The cached delegate, so every test's options look alike to EF's internal provider cache.
            options.UseSqlite(connection, retrying ? TestRetryingStrategy.Configure : null);

            if (order == FollowUpOrder.Before)
                options.AddInterceptors(new FollowUpSaveInterceptor(probe));

            options.UseMultiLingual(sp);

            if (order == FollowUpOrder.After)
                options.AddInterceptors(new FollowUpSaveInterceptor(probe));

            if (extra is not null)
                options.AddInterceptors(extra);
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

            // With OwnTransactions(false) a save that writes translations needs the caller's
            // transaction, inside the strategy when one retries.
            if (_ownTransactions)
            {
                await scope.Db.SaveChangesAsync();
            }
            else
            {
                await scope.Db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var transaction = await scope.Db.Database.BeginTransactionAsync();
                    await scope.Db.SaveChangesAsync();
                    await transaction.CommitAsync();
                });
            }

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
    internal const string Gap2 = "COMPOSITION-GAPS.md gap 2: the owned transaction is committed before later interceptors run.";
    internal const string Gap2Retrying =
        "COMPOSITION-GAPS.md gap 2: under a retrying strategy, an interceptor registered first completes after the strategy's commit.";

    // ----------------------------------------------------------------- transaction

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
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
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
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

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true, Skip = Gap2Retrying)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task FailureOfTheFollowUpSave_RollsBackEverything(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying);
        shop.Probe.FailTheFollowUpSave = true;

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Price = 1, Name = "Souris" });
            await Assert.ThrowsAsync<DbUpdateException>(() => scope.Db.SaveChangesAsync());
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
    [InlineData(FollowUpOrder.Before, false)]
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

    // ----------------------------------------------------------------- the deferred commit

    /// <summary>
    /// Registered after the follow-up, the commit waits for EF's <c>SavedChanges</c> event. A commit
    /// failing there must leave nothing stored, and the save retryable.
    /// </summary>
    [Fact]
    public async Task ACommitFailingAfterTheFollowUp_StoresNothing_AndTheSaveCanBeRetried()
    {
        var commits = new FailCommits();
        await using var shop = await ComposedShop.StartAsync(FollowUpOrder.After, retrying: false, extra: [commits]);

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Price = 1, Name = "Souris" });
            commits.Arm(1);
            await Assert.ThrowsAsync<TransientTestException>(() => scope.Db.SaveChangesAsync());
            Assert.Null(scope.Db.Database.CurrentTransaction);
            Assert.Equal((0, 0, 0), await shop.StoredCountsAsync());

            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal([("fr", (string?)"Souris")], await shop.StoredNamesAsync());
        Assert.Equal((1, 1, 1), await shop.StoredCountsAsync());
    }

    /// <summary>
    /// Translations a follow-up save adds are written too — while the deferred commit waits
    /// (registered after), or with the outer save's own (registered before).
    /// </summary>
    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task TranslationsTheFollowUpSaveAdds_AreWrittenInTheSameTransaction(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying);
        shop.Probe.AddProductInFollowUp = true;

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Price = 1, Name = "Souris" });
            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal([("fr", (string?)"Note"), ("fr", "Souris")], (await shop.StoredNamesAsync()).OrderBy(n => n.Value).ToList());
        Assert.Equal((2, 2, 1), await shop.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task TranslationsTheFollowUpSaveAdds_RollBackWithEverythingElse(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying);
        shop.Probe.AddProductInFollowUp = true;
        shop.Probe.FailAfterFollowUp    = true;

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Price = 1, Name = "Souris" });
            await Assert.ThrowsAsync<FollowUpFailedException>(() => scope.Db.SaveChangesAsync());
        }

        Assert.Equal((0, 0, 0), await shop.StoredCountsAsync());
    }

    /// <summary>With no SaveChanges interceptor after this one, nothing waits: the commit is the one it always was.</summary>
    [Fact]
    public async Task WithNoInterceptorAfterThisOne_TheCommitIsNotDeferred()
    {
        var commits = new FailCommits();
        await using var shop = await ComposedShop.StartAsync(FollowUpOrder.Before, retrying: false, extra: [commits]);

        await using var scope = await shop.ScopeInAsync("fr");
        var committedInSavedChanges = false;
        scope.Db.SavedChanges += (_, _) => committedInSavedChanges = scope.Db.Database.CurrentTransaction is null;

        scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Price = 1, Name = "Souris" });
        await scope.Db.SaveChangesAsync();

        Assert.True(committedInSavedChanges);
    }

    // ----------------------------------------------------------------- OwnTransactions(false)

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task OwnTransactionsOff_WithoutACallerTransaction_RefusesTheSave_BeforeWritingAnything(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying, ownTransactions: false);

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Price = 1, Name = "Souris" });
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Db.SaveChangesAsync());

            Assert.Contains("OwnTransactions(false)", ex.Message, StringComparison.Ordinal);
            Assert.Contains("docs/limitations.md", ex.Message, StringComparison.Ordinal);
            Assert.Null(scope.Db.Database.CurrentTransaction);
        }

        Assert.Empty(shop.Probe.RanInsideTransaction);
        Assert.Equal((0, 0, 0), await shop.StoredCountsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnTransactionsOff_ASaveWithNoTranslationWork_IsNotRefused(bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(FollowUpOrder.After, retrying, ownTransactions: false);
        var id = await shop.SeedProductAsync("Laptop", ("fr", "Ordinateur"));

        await using (var scope = await shop.ScopeInAsync("ar"))
        {
            var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
            product.Price = 20;
            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal([("en", (string?)"Laptop"), ("fr", "Ordinateur")], await shop.StoredNamesAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before)]
    [InlineData(FollowUpOrder.After)]
    public async Task TheDocumentedAppTransaction_RunsEverythingInside(FollowUpOrder order)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying: false, ownTransactions: false, appTransactions: true);

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Price = 1, Name = "Souris" });
            await scope.Db.SaveChangesAsync();
        }

        Assert.Equal([true], shop.Probe.RanInsideTransaction);
        Assert.Equal([("fr", (string?)"Souris")], await shop.StoredNamesAsync());
        Assert.Equal((1, 1, 1), await shop.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before)]
    [InlineData(FollowUpOrder.After)]
    public async Task TheDocumentedAppTransaction_RollsBackEverything(FollowUpOrder order)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying: false, ownTransactions: false, appTransactions: true);
        shop.Probe.FailAfterFollowUp = true;

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Price = 1, Name = "Souris" });
            await Assert.ThrowsAsync<FollowUpFailedException>(() => scope.Db.SaveChangesAsync());
        }

        Assert.Equal((0, 0, 0), await shop.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before)]
    [InlineData(FollowUpOrder.After)]
    public async Task TheDocumentedAppTransaction_Delete_RemovesTheTranslations(FollowUpOrder order)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying: false, ownTransactions: false, appTransactions: true);
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

    // ----------------------------------------------------------------- recovery: saves EF never reports as failed

    /// <summary>
    /// An interceptor after ours whose <c>SavingChanges</c> throws: EF then reports no failure to
    /// anyone, so this save never ends as far as the count knows. The next one must not be taken
    /// for a save nested inside it.
    /// </summary>
    [Fact]
    public async Task AfterALaterInterceptorRefusesASave_TheNextSave_StillWritesItsTranslations()
    {
        var refuse = new RefuseNextSave();
        await using var shop  = await Shop.StartAsync(null, refuse);
        await using var scope = await shop.ScopeInAsync("fr");

        var product = new ShopProduct { Sku = "MOUSE", Name = "Souris" };
        scope.Db.Products.Add(product);
        refuse.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Db.SaveChangesAsync());

        await scope.Db.SaveChangesAsync();

        var row = Assert.Single(await shop.StoredTranslationsAsync());
        Assert.Equal(("fr", "Souris"), (row.LanguageCode, row.Value));
        Assert.Null(scope.Db.Database.CurrentTransaction);
    }

    /// <summary>The same, for a save that changes only translatable text: no entry EF tracks changes.</summary>
    [Fact]
    public async Task AfterALaterInterceptorRefusesATextOnlySave_TheNextSave_StillWritesItsTranslations()
    {
        var refuse = new RefuseNextSave();
        await using var shop = await Shop.StartAsync(null, refuse);

        int id;
        await using (var seed = await shop.ScopeInAsync("en"))
        {
            var created = new ShopProduct { Sku = "MOUSE", Name = "Mouse" };
            seed.Db.Products.Add(created);
            await seed.Db.SaveChangesAsync();
            id = created.Id;
        }

        await using var scope = await shop.ScopeInAsync("fr");
        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
        product.Name = "Souris";
        refuse.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Db.SaveChangesAsync());

        await scope.Db.SaveChangesAsync();

        Assert.Contains(await shop.StoredTranslationsAsync(), t => t is { LanguageCode: "fr", Value: "Souris" });
        Assert.Null(scope.Db.Database.CurrentTransaction);
    }

    /// <summary>EF raises no <c>SaveChangesFailed</c> for a concurrency conflict: the transaction must not be left open.</summary>
    [Fact]
    public async Task AConcurrencyConflict_RollsBackTheOwnedTransaction_AndTheNextSaveWorks()
    {
        await using var shop = await Shop.StartAsync();
        int id;
        await using (var seed = await shop.ScopeInAsync("en"))
        {
            var created = new ShopProduct { Sku = "MOUSE", Name = "Mouse" };
            seed.Db.Products.Add(created);
            await seed.Db.SaveChangesAsync();
            id = created.Id;
        }

        await using var scope = await shop.ScopeInAsync("fr");
        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
        await scope.Db.Database.ExecuteSqlAsync($"DELETE FROM Products WHERE Id = {id}");

        product.Price = 2;
        product.Name  = "Souris";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => scope.Db.SaveChangesAsync());
        Assert.Null(scope.Db.Database.CurrentTransaction);

        scope.Db.Entry(product).State = EntityState.Detached;
        scope.Db.Products.Add(new ShopProduct { Sku = "KEYBOARD", Name = "Clavier" });
        await scope.Db.SaveChangesAsync();

        Assert.Contains(await shop.StoredTranslationsAsync(), t => t is { LanguageCode: "fr", Value: "Clavier" });
        Assert.DoesNotContain(await shop.StoredTranslationsAsync(), t => t.Value == "Souris");
    }
}

/// <summary>Refuses the next save from its <c>SavingChanges</c>, once armed.</summary>
internal sealed class RefuseNextSave : SaveChangesInterceptor
{
    public bool Armed { get; set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!Armed)
            return ValueTask.FromResult(result);

        Armed = false;
        throw new InvalidOperationException("Refused by a later interceptor.");
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (!Armed)
            return result;

        Armed = false;
        throw new InvalidOperationException("Refused by a later interceptor.");
    }
}

/// <summary>
/// The synchronous save beside the follow-up interceptor. The save interceptor has a synchronous
/// member for every step, so each composition and recovery case runs again through <c>SaveChanges()</c>.
/// </summary>
public sealed class SyncSaveCompositionTests
{
    private const string Gap2 = SaveCompositionTests.Gap2;
    private const string Gap2Retrying = SaveCompositionTests.Gap2Retrying;

    private static ShopProduct Mouse() => new() { Sku = "MOUSE", Price = 1, Name = "Souris" };

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true, Skip = Gap2Retrying)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task Create_RunsTheFollowUpInsideTheTransaction(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying);

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(Mouse());
            scope.Db.SaveChanges();
        }

        Assert.Equal([true], shop.Probe.RanInsideTransaction);
        Assert.Equal([("fr", (string?)"Souris")], await shop.StoredNamesAsync());
        Assert.Equal((1, 1, 1), await shop.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true, Skip = Gap2Retrying)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task FailureAfterTheFollowUp_RollsBackEverything(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying);
        shop.Probe.FailAfterFollowUp = true;

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(Mouse());
            Assert.Throws<FollowUpFailedException>(() => scope.Db.SaveChanges());
        }

        Assert.Equal((0, 0, 0), await shop.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true, Skip = Gap2Retrying)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task FailureOfTheFollowUpSave_RollsBackEverything(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying);
        shop.Probe.FailTheFollowUpSave = true;

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(Mouse());
            Assert.Throws<DbUpdateException>(() => scope.Db.SaveChanges());
        }

        Assert.Equal((0, 0, 0), await shop.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task Delete_RemovesTheTranslations(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying);
        var id = await shop.SeedProductAsync("Laptop", ("fr", "Ordinateur"));

        await using (var scope = shop.Scope())
        {
            scope.Db.Products.Remove(await scope.Db.Products.SingleAsync(p => p.Id == id));
            scope.Db.SaveChanges();
        }

        Assert.Empty(await shop.StoredNamesAsync());
        Assert.Equal((0, 0, 1), await shop.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before, false)]
    [InlineData(FollowUpOrder.After, false)]
    [InlineData(FollowUpOrder.Before, true)]
    [InlineData(FollowUpOrder.After, true)]
    public async Task OwnTransactionsOff_WithoutACallerTransaction_RefusesTheSave_BeforeWritingAnything(FollowUpOrder order, bool retrying)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying, ownTransactions: false);

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(Mouse());
            var ex = Assert.Throws<InvalidOperationException>(() => scope.Db.SaveChanges());

            Assert.Contains("OwnTransactions(false)", ex.Message, StringComparison.Ordinal);
            Assert.Null(scope.Db.Database.CurrentTransaction);
        }

        Assert.Empty(shop.Probe.RanInsideTransaction);
        Assert.Equal((0, 0, 0), await shop.StoredCountsAsync());
    }

    [Theory]
    [InlineData(FollowUpOrder.Before)]
    [InlineData(FollowUpOrder.After)]
    public async Task TheDocumentedAppTransaction_RollsBackEverything(FollowUpOrder order)
    {
        await using var shop = await ComposedShop.StartAsync(order, retrying: false, ownTransactions: false, appTransactions: true);
        shop.Probe.FailAfterFollowUp = true;

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(Mouse());
            Assert.Throws<FollowUpFailedException>(() => scope.Db.SaveChanges());
        }

        Assert.Equal([true], shop.Probe.RanInsideTransaction);
        Assert.Equal((0, 0, 0), await shop.StoredCountsAsync());
    }

    [Fact]
    public async Task ACommitFailingAfterTheFollowUp_StoresNothing_AndTheSaveCanBeRetried()
    {
        var commits = new FailCommits();
        await using var shop = await ComposedShop.StartAsync(FollowUpOrder.After, retrying: false, extra: [commits]);

        await using (var scope = await shop.ScopeInAsync("fr"))
        {
            scope.Db.Products.Add(Mouse());
            commits.Arm(1);
            Assert.Throws<TransientTestException>(() => scope.Db.SaveChanges());
            Assert.Null(scope.Db.Database.CurrentTransaction);
            Assert.Equal((0, 0, 0), await shop.StoredCountsAsync());

            scope.Db.SaveChanges();
        }

        Assert.Equal([("fr", (string?)"Souris")], await shop.StoredNamesAsync());
        Assert.Equal((1, 1, 1), await shop.StoredCountsAsync());
    }

    [Fact]
    public async Task AfterALaterInterceptorRefusesASave_TheNextSave_StillWritesItsTranslations()
    {
        var refuse = new RefuseNextSave();
        await using var shop  = await Shop.StartAsync(null, refuse);
        await using var scope = await shop.ScopeInAsync("fr");

        scope.Db.Products.Add(new ShopProduct { Sku = "MOUSE", Name = "Souris" });
        refuse.Armed = true;
        Assert.Throws<InvalidOperationException>(() => scope.Db.SaveChanges());

        scope.Db.SaveChanges();

        var row = Assert.Single(await shop.StoredTranslationsAsync());
        Assert.Equal(("fr", "Souris"), (row.LanguageCode, row.Value));
        Assert.Null(scope.Db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task AConcurrencyConflict_RollsBackTheOwnedTransaction_AndTheNextSaveWorks()
    {
        await using var shop = await Shop.StartAsync();
        int id;
        await using (var seed = await shop.ScopeInAsync("en"))
        {
            var created = new ShopProduct { Sku = "MOUSE", Name = "Mouse" };
            seed.Db.Products.Add(created);
            await seed.Db.SaveChangesAsync();
            id = created.Id;
        }

        await using var scope = await shop.ScopeInAsync("fr");
        var product = await scope.Db.Products.SingleAsync(p => p.Id == id);
        await scope.Db.Database.ExecuteSqlAsync($"DELETE FROM Products WHERE Id = {id}");

        product.Price = 2;
        product.Name  = "Souris";
        Assert.Throws<DbUpdateConcurrencyException>(() => scope.Db.SaveChanges());
        Assert.Null(scope.Db.Database.CurrentTransaction);

        scope.Db.Entry(product).State = EntityState.Detached;
        scope.Db.Products.Add(new ShopProduct { Sku = "KEYBOARD", Name = "Clavier" });
        scope.Db.SaveChanges();

        Assert.Contains(await shop.StoredTranslationsAsync(), t => t is { LanguageCode: "fr", Value: "Clavier" });
        Assert.DoesNotContain(await shop.StoredTranslationsAsync(), t => t.Value == "Souris");
    }
}
