global using Xunit;
using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Attributes;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.UnitTests;

public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;

    [MultiLingual] public string? Name { get; set; }
    [MultiLingual] public string? Description { get; set; }
}

[MultiLingual]
public class Category
{
    public int Id { get; set; }
    public string? Name { get; set; }
    [NotMultiLingual] public string Slug { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
}

public class Document
{
    public Guid Id { get; set; }
    [MultiLingual] public string? Title { get; set; }
}

public class Plain
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

/// <summary>A clock tests can move.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// A real container over the in-memory stores: the engine is exercised through the same
/// registrations an application gets, not through hand-assembled internals.
/// </summary>
internal sealed class Engine : IAsyncDisposable
{
    private readonly ServiceProvider _root;

    public InMemoryTranslationStore Translations { get; }
    public InMemoryLanguageStore Languages { get; }
    public ManualTimeProvider Clock { get; } = new();

    private Engine(Action<MultiLingualBuilder>? configure, Engine? sharingStoresWith, Action<IServiceCollection>? configureServices)
    {
        Translations = sharingStoresWith?.Translations ?? new();
        Languages    = sharingStoresWith?.Languages ?? new();

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        configureServices?.Invoke(services);

        var builder = services.AddMultiLingual().UseCustomStores(_ => Translations, _ => Languages);
        configure?.Invoke(builder);
        builder.Build();

        _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>An engine with en (default), ar and fr.</summary>
    public static async Task<Engine> StartAsync(Action<MultiLingualBuilder>? configure = null, bool seedLanguages = true)
    {
        var engine = new Engine(configure, sharingStoresWith: null, configureServices: null);
        if (seedLanguages)
        {
            await using var scope = engine.Scope();
            var manager = scope.Get<ILanguageManager>();
            await manager.CreateAsync(new NewLanguage { Code = "en", Name = "English" });
            await manager.CreateAsync(new NewLanguage { Code = "ar", Name = "Arabic" });
            await manager.CreateAsync(new NewLanguage { Code = "fr", Name = "French" });
        }

        return engine;
    }

    /// <summary>
    /// Another application instance over the same stores — a second server behind the load
    /// balancer. Its own container, catalog and clock; <paramref name="configureServices"/> can
    /// give it whatever else the instances share, such as a distributed cache.
    /// </summary>
    public Engine StartSibling(Action<MultiLingualBuilder>? configure = null, Action<IServiceCollection>? configureServices = null) =>
        new(configure, sharingStoresWith: this, configureServices);

    /// <summary>An engine with en (default), ar and fr, and extra registrations.</summary>
    public static async Task<Engine> StartAsync(Action<MultiLingualBuilder>? configure, Action<IServiceCollection> configureServices)
    {
        var engine = new Engine(configure, sharingStoresWith: null, configureServices);
        await using var scope = engine.Scope();
        var manager = scope.Get<ILanguageManager>();
        await manager.CreateAsync(new NewLanguage { Code = "en", Name = "English" });
        await manager.CreateAsync(new NewLanguage { Code = "ar", Name = "Arabic" });
        await manager.CreateAsync(new NewLanguage { Code = "fr", Name = "French" });
        return engine;
    }

    public TestScope Scope() => new(_root.CreateAsyncScope());

    /// <summary>A scope whose language context is already resolved, as middleware would leave it.</summary>
    public async Task<TestScope> ScopeInAsync(string languageCode)
    {
        var scope = Scope();
        await scope.Get<ILanguageContext>().UseAsync(languageCode);
        return scope;
    }

    public T GetSingleton<T>() where T : notnull => _root.GetRequiredService<T>();

    public ValueTask DisposeAsync() => _root.DisposeAsync();
}

internal sealed class TestScope(AsyncServiceScope scope) : IAsyncDisposable
{
    public T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public ITranslationManager Translations => Get<ITranslationManager>();

    public ITranslationWriter Writer => Get<ITranslationWriter>();

    public ValueTask DisposeAsync() => scope.DisposeAsync();
}
