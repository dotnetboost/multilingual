using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.UnitTests;

/// <summary>
/// <c>SetAll</c> and <c>GetAllAsync(includeBase: true)</c> without a change tracker — the engine as
/// Dapper and MongoDB drive it: the application saves the entity's row, then calls
/// <c>For(entity).SaveAsync()</c>.
/// </summary>
public sealed class AllLanguagesTests
{
    private static Dictionary<string, IReadOnlyDictionary<string, string?>> Names(params (string Code, string? Name)[] names) =>
        names.ToDictionary(n => n.Code, n => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["Name"] = n.Name });

    [Fact]
    public async Task SetAll_ThenSaveAsync_UnderAnotherLanguage_DoesNotFileTheBaseTextAsThatLanguage()
    {
        await using var engine = await Engine.StartAsync(b => b.WithBaseLanguage("en"));
        await using var scope  = await engine.ScopeInAsync("ar");

        var product = new Product { Id = 1 };
        scope.MultiLingual.For(product).SetAll(Names(("en", "Laptop"), ("ar", "حاسوب")));
        await scope.MultiLingual.For(product).SaveAsync();

        Assert.Equal("Laptop", product.Name);   // what the application writes to the column
        var row = Assert.Single(engine.Translations.All);
        Assert.Equal(("ar", "حاسوب"), (row.LanguageCode, row.Value));
    }

    [Fact]
    public async Task GetAllIncludingBase_AfterLocalizing_AnswersWhatTheEntityWasLoadedWith()
    {
        await using var engine = await Engine.StartAsync(b => b.WithBaseLanguage("en"));
        await using (var seed = await engine.ScopeInAsync("ar"))
        {
            await seed.MultiLingual.For(new Product { Id = 1 }).Set("ar", p => p.Name, "حاسوب").SaveAsync();
        }

        await using var scope = await engine.ScopeInAsync("ar");
        var product = new Product { Id = 1, Name = "Laptop" };   // as loaded from its row
        await scope.MultiLingual.For(product).LocalizeAsync();
        Assert.Equal("حاسوب", product.Name);

        var all = await scope.MultiLingual.For(product).GetAllAsync(includeBase: true);

        Assert.Equal("Laptop", all["en"]["Name"]);
        Assert.Equal("حاسوب", all["ar"]["Name"]);
    }

    [Fact]
    public async Task GetAllIncludingBase_NeverLocalized_AnswersTheProperties()
    {
        await using var engine = await Engine.StartAsync(b => b.WithBaseLanguage("en"));
        await using var scope  = await engine.ScopeInAsync("ar");

        var product = new Product { Id = 1, Name = "Laptop" };
        var all = await scope.MultiLingual.For(product).GetAllAsync(includeBase: true);

        Assert.Equal(["en"], all.Keys);
        Assert.Equal("Laptop", all["en"]["Name"]);
    }

    [Fact]
    public async Task GetAllIncludingBase_AfterSetAll_AnswersWhatSetAllAssigned()
    {
        await using var engine = await Engine.StartAsync(b => b.WithBaseLanguage("en"));
        await using var scope  = await engine.ScopeInAsync("ar");

        var product = new Product { Id = 1, Name = "Laptop" };
        await scope.MultiLingual.For(product).LocalizeAsync();
        scope.MultiLingual.For(product).SetAll(Names(("en", "Laptop Pro")));

        Assert.Equal("Laptop Pro", (await scope.MultiLingual.For(product).GetAllAsync(includeBase: true))["en"]["Name"]);
    }

    [Fact]
    public async Task GetAllIncludingBase_OnAnEntityWithoutAKey_IsEmpty()
    {
        await using var engine = await Engine.StartAsync(b => b.WithBaseLanguage("en"));
        await using var scope  = await engine.ScopeInAsync("ar");

        Assert.Empty(await scope.MultiLingual.For(new Product { Name = "Laptop" }).GetAllAsync(includeBase: true));
    }
}
