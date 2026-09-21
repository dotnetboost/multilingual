using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotNetBoost.MultiLingual.UnitTests;

/// <summary>A store that cannot be read — an un-migrated database, at startup.</summary>
internal sealed class UnreachableLanguageStore : ILanguageStore
{
    private static InvalidOperationException Down() => new("no such table: Languages");

    public Task<IReadOnlyList<Language>> GetAllAsync(CancellationToken ct = default) => throw Down();
    public Task<Language?> FindAsync(string code, CancellationToken ct = default) => throw Down();
    public Task AddAsync(Language language, CancellationToken ct = default) => throw Down();
    public Task UpdateAsync(Language language, CancellationToken ct = default) => throw Down();
    public Task DeleteAsync(string code, CancellationToken ct = default) => throw Down();
    public Task SetDefaultAsync(string code, CancellationToken ct = default) => throw Down();
}

public sealed class BaseLanguageValidationTests
{
    private static ServiceProvider Build(Action<MultiLingualOptions> configure, ILanguageStore languages)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMultiLingual(configure)
            .UseCustomStores(_ => new InMemoryTranslationStore(), _ => languages)
            .Build();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    // ----------------------------------------------------------------- the check itself

    [Fact]
    public async Task Passes_WhenTheBaseLanguageExistsAndIsActive()
    {
        await using var engine = await Engine.StartAsync(b => b.WithBaseLanguage("en"));

        await engine.GetSingleton<IServiceProvider>().ValidateMultiLingualAsync();
    }

    [Fact]
    public async Task Throws_WhenTheBaseLanguageDoesNotExist()
    {
        await using var engine = await Engine.StartAsync(b => b.WithBaseLanguage("de"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.GetSingleton<IServiceProvider>().ValidateMultiLingualAsync());

        Assert.Contains("'de'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ar, en, fr", ex.Message, StringComparison.Ordinal);   // what does exist
    }

    [Fact]
    public async Task Throws_WhenTheBaseLanguageIsInactive()
    {
        await using var engine = await Engine.StartAsync(b => b.WithBaseLanguage("fr"));

        // Straight at the store: ILanguageManager refuses to deactivate the base language, which
        // is exactly why this state can only arrive from outside the library.
        await engine.Languages.UpdateAsync(new Language
        {
            Code = "fr", Name = "French", Direction = TextDirection.Ltr, IsActive = false
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.GetSingleton<IServiceProvider>().ValidateMultiLingualAsync());

        Assert.Contains("inactive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IsANoOp_WhenNoBaseLanguageIsConfigured()
    {
        await using var engine = await Engine.StartAsync();
        var before = engine.Languages.GetAllCalls;

        await engine.GetSingleton<IServiceProvider>().ValidateMultiLingualAsync();

        Assert.Equal(before, engine.Languages.GetAllCalls);   // the store is not even read
    }

    // ----------------------------------------------------------------- what it tolerates

    [Fact]
    public async Task DoesNotThrow_WhenNoLanguagesExistYet()
    {
        // Refusing to start here would be a deadlock: creating that first language is what the
        // application's own endpoints are for.
        await using var engine = await Engine.StartAsync(b => b.WithBaseLanguage("en"), seedLanguages: false);

        await engine.GetSingleton<IServiceProvider>().ValidateMultiLingualAsync();
    }

    [Fact]
    public async Task DoesNotThrow_WhenTheStoreCannotBeRead()
    {
        await using var root = Build(o => o.BaseLanguage = "en", new UnreachableLanguageStore());

        await root.ValidateMultiLingualAsync();
    }

    // ----------------------------------------------------------------- when it runs

    [Fact]
    public void RunsAtStartup_WhenABaseLanguageIsConfigured()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMultiLingual(o => o.BaseLanguage = "en")
            .UseCustomStores(_ => new InMemoryTranslationStore(), _ => new InMemoryLanguageStore())
            .Build();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(MultiLingualValidationHostedService));
    }

    [Fact]
    public void DoesNotRunAtStartup_WithoutABaseLanguage()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMultiLingual()
            .UseCustomStores(_ => new InMemoryTranslationStore(), _ => new InMemoryLanguageStore())
            .Build();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void DoesNotRunAtStartup_WhenDisabled()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMultiLingual(o => o.BaseLanguage = "en")
            .UseCustomStores(_ => new InMemoryTranslationStore(), _ => new InMemoryLanguageStore())
            .DisableStartupValidation()
            .Build();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public async Task TheHostedService_RunsTheCheck()
    {
        await using var root = Build(o => o.BaseLanguage = "de", new InMemoryLanguageStore());

        await using (var scope = root.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ILanguageManager>()
                .CreateAsync(new NewLanguage { Code = "en", Name = "English" });
        }

        var hosted = Assert.Single(root.GetServices<IHostedService>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => hosted.StartAsync(CancellationToken.None));
    }
}
