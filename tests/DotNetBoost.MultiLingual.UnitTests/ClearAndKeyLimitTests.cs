using DotNetBoost.MultiLingual.Core.Attributes;
using DotNetBoost.MultiLingual.Core.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.UnitTests;

/// <summary>Internal, not public: Build() validates exported types only, and this one is fine anyway.</summary>
internal sealed class Slugged
{
    public string Id { get; set; } = string.Empty;
    [MultiLingual] public string? Name { get; set; }
}

/// <summary>Removing one stored translation, and keys too long to store.</summary>
public sealed class ClearAndKeyLimitTests
{
    /// <summary>Product 1: Name in en, ar and fr; Description in en and fr.</summary>
    private static async Task SeedAsync(Engine engine)
    {
        await using var scope = engine.Scope();
        var product = new Product { Id = 1 };
        scope.Translations.Stage(product, "en", new Dictionary<string, string?> { ["Name"] = "Laptop", ["Description"] = "Light" });
        scope.Translations.Stage(product, "ar", new Dictionary<string, string?> { ["Name"] = "حاسوب" });
        scope.Translations.Stage(product, "fr", new Dictionary<string, string?> { ["Name"] = "Portable", ["Description"] = "Léger" });
        await scope.Translations.SaveAsync(product);
    }

    private static string[] Stored(Engine engine) =>
        engine.Translations.All.Select(t => $"{t.LanguageCode}.{t.PropertyName}").Order(StringComparer.Ordinal).ToArray();

    // ================================================================== clear

    [Fact]
    public async Task Clear_OneProperty_RemovesOnlyThatRow()
    {
        await using var engine = await Engine.StartAsync();
        await SeedAsync(engine);
        await using var scope = engine.Scope();

        var product = new Product { Id = 1 };
        scope.Translations.Clear(product, "fr", nameof(Product.Name));
        await scope.Translations.SaveAsync(product);

        Assert.Equal(["ar.Name", "en.Description", "en.Name", "fr.Description"], Stored(engine));
    }

    [Fact]
    public async Task Clear_WithoutPropertyNames_RemovesTheWholeLanguage_ForThatEntity()
    {
        await using var engine = await Engine.StartAsync();
        await SeedAsync(engine);
        await using var scope = engine.Scope();

        var product = new Product { Id = 1 };
        scope.Translations.Clear(product, "fr");
        await scope.Translations.SaveAsync(product);

        Assert.Equal(["ar.Name", "en.Description", "en.Name"], Stored(engine));
    }

    [Fact]
    public async Task AfterClearing_ReadsFallBack()
    {
        await using var engine = await Engine.StartAsync();
        await SeedAsync(engine);

        await using (var write = engine.Scope())
        {
            var product = new Product { Id = 1 };
            write.Translations.Clear(product, "fr", nameof(Product.Name));
            await write.Translations.SaveAsync(product);
        }

        await using var read = await engine.ScopeInAsync("fr");
        var loaded = new Product { Id = 1 };
        await read.Translations.HydrateAsync([loaded]);

        Assert.Equal("Laptop", loaded.Name);        // the default language
        Assert.Equal("Léger", loaded.Description);  // untouched
    }

    [Fact]
    public async Task NullProperty_StillMeansNotSupplied()
    {
        await using var engine = await Engine.StartAsync();
        await SeedAsync(engine);
        await using var scope = await engine.ScopeInAsync("fr");

        await scope.Translations.SaveAsync(new Product { Id = 1, Name = null });

        Assert.Contains("fr.Name", Stored(engine));
    }

    [Fact]
    public async Task Clear_UnknownProperty_IsRefused()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = engine.Scope();

        Assert.Throws<ArgumentException>(() => scope.Translations.Clear(new Product { Id = 1 }, "fr", nameof(Product.Sku)));
    }

    [Fact]
    public async Task Clear_TheBaseLanguage_IsRefused()
    {
        await using var engine = await Engine.StartAsync(b => b.WithBaseLanguage("en"));
        await using var scope = engine.Scope();

        Assert.Throws<ArgumentException>(() => scope.Translations.Clear(new Product { Id = 1 }, "en"));
    }

    // ================================================================== key limit

    [Fact]
    public async Task Save_KeyLongerThanTheColumn_IsRefused_WithTheEntityNamed()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Translations.SaveAsync(new Slugged { Id = new string('x', EntityKey.MaxLength + 1), Name = "n" }));

        Assert.Contains(nameof(Slugged), ex.Message, StringComparison.Ordinal);
        Assert.Contains("64", ex.Message, StringComparison.Ordinal);
        Assert.Empty(engine.Translations.All);
    }

    [Fact]
    public async Task Save_KeyOfExactlyTheMaximum_IsStored()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        await scope.Translations.SaveAsync(new Slugged { Id = new string('x', EntityKey.MaxLength), Name = "n" });

        Assert.Single(engine.Translations.All);
    }
}
