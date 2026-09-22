using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace DotNetBoost.MultiLingual.UnitTests;

/// <summary>
/// <see cref="Core.Interfaces.IMultiLingualManager"/> — the one service applications inject —
/// over the in-memory stores. With no change tracker this is exactly how a Dapper or MongoDB
/// application uses the library: query, localize, save and delete through the manager.
/// </summary>
public sealed class MultiLingualManagerTests
{
    // ------------------------------------------------------------------ untracked provider flow

    [Fact]
    public async Task WithoutAChangeTracker_SaveLocalizeAndDelete_GoThroughTheManager()
    {
        await using var engine = await Engine.StartAsync();

        await using (var write = await engine.ScopeInAsync("en"))
        {
            // As after "INSERT INTO Products …" with Dapper: the row exists, so the key does.
            var product = new Product { Id = 1, Name = "Laptop", Description = "Light" };
            write.MultiLingual.For(product).Set("ar", p => p.Name, "حاسوب");
            await write.MultiLingual.For(product).SaveAsync();
        }

        await using (var read = await engine.ScopeInAsync("ar"))
        {
            // As after "SELECT * FROM Products": translatable properties have no column.
            var product = new Product { Id = 1 };
            await read.MultiLingual.LocalizeAsync([product]);

            Assert.Equal("حاسوب", product.Name);
            Assert.Equal("Light", product.Description);   // no Arabic: falls back to English

            var stored = await read.MultiLingual.For(product).GetAllAsync();
            Assert.Equal("Laptop", stored["en"]["Name"]);
            Assert.Equal("حاسوب", stored["ar"]["Name"]);

            await read.MultiLingual.For(product).DeleteAsync();
            Assert.Empty(await read.MultiLingual.For(product).GetAllAsync());
        }
    }

    [Fact]
    public async Task Localize_InAGivenLanguage_AndSynchronously_UseTheSameChain()
    {
        await using var engine = await Engine.StartAsync();
        await SeedLaptopAsync(engine);
        await using var scope = engine.Scope();

        var french = new Product { Id = 1 };
        await scope.MultiLingual.LocalizeAsync([french], "fr");
        Assert.Equal("Ordinateur", french.Name);

        var arabic = new Product { Id = 1 };
        scope.MultiLingual.Localize([arabic], "ar");
        Assert.Equal(("حاسوب", "Light"), (arabic.Name, arabic.Description));

        var byDefault = new Product { Id = 1 };
        scope.MultiLingual.Localize([byDefault]);
        Assert.Equal("Laptop", byDefault.Name);

        var single = new Product { Id = 1 };
        await scope.MultiLingual.For(single).LocalizeAsync("fr");
        Assert.Equal("Ordinateur", single.Name);

        await Assert.ThrowsAsync<UnknownLanguageException>(() => scope.MultiLingual.LocalizeAsync([new Product { Id = 1 }], "de"));
    }

    // ------------------------------------------------------------------ set / clear

    [Fact]
    public async Task Set_ByExpressionAndByName_QueuesForTheNextSave()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        var product = new Product { Id = 7 };
        scope.MultiLingual.For(product)
            .Set("ar", p => p.Name, "كرسي")
            .Set("FR", new Dictionary<string, string?> { ["Name"] = "Chaise", ["Description"] = "En bois" });

        Assert.Empty(engine.Translations.All);   // only queued

        await scope.MultiLingual.For(product).SaveAsync();

        var stored = await scope.MultiLingual.For(product).GetAllAsync();
        Assert.Equal("كرسي", stored["ar"]["Name"]);
        Assert.Equal("Chaise", stored["fr"]["Name"]);   // stored under the canonical code
        Assert.Equal("En bois", stored["fr"]["Description"]);
    }

    [Fact]
    public async Task Clear_AWholeLanguage_OrSomeProperties()
    {
        await using var engine = await Engine.StartAsync();
        await SeedLaptopAsync(engine);
        await using var scope = await engine.ScopeInAsync("en");

        var product = new Product { Id = 1 };
        scope.MultiLingual.For(product).Clear("fr", p => p.Description);
        await scope.MultiLingual.For(product).SaveAsync();

        var stored = await scope.MultiLingual.For(product).GetAllAsync();
        Assert.Equal("Ordinateur", stored["fr"]["Name"]);
        Assert.False(stored["fr"].ContainsKey("Description"));

        scope.MultiLingual.For(product).Clear("ar");
        await scope.MultiLingual.For(product).SaveAsync();

        stored = await scope.MultiLingual.For(product).GetAllAsync();
        Assert.False(stored.ContainsKey("ar"));
        Assert.True(stored.ContainsKey("en"));
    }

    [Fact]
    public async Task Clear_WithAnEmptyPropertyList_QueuesNothing()
    {
        await using var engine = await Engine.StartAsync();
        await SeedLaptopAsync(engine);
        await using var scope = await engine.ScopeInAsync("en");

        var product = new Product { Id = 1 };
        scope.MultiLingual.For(product).Clear("ar", Array.Empty<string>());
        await scope.MultiLingual.For(product).SaveAsync();

        Assert.Equal("حاسوب", (await scope.MultiLingual.For(product).GetAllAsync())["ar"]["Name"]);
    }

    [Fact]
    public async Task SetAndClear_AnUnknownLanguage_AreRefusedAtOnce()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");
        var product = new Product { Id = 1 };

        var set   = Assert.Throws<LanguageNotFoundException>(() => scope.MultiLingual.For(product).Set("de", p => p.Name, "Stuhl"));
        var clear = Assert.Throws<LanguageNotFoundException>(() => scope.MultiLingual.For(product).Clear("de"));

        Assert.Equal("de", set.Code);
        Assert.Equal("de", clear.Code);
    }

    [Fact]
    public async Task SetAndClear_InTheBaseLanguage_AreRefused()
    {
        await using var engine = await Engine.StartAsync(b => b.WithBaseLanguage("en"));
        await using var scope = await engine.ScopeInAsync("ar");
        var product = new Product { Id = 1 };

        var clear = Assert.Throws<BaseLanguageNotTranslatableException>(() => scope.MultiLingual.For(product).Clear("EN"));
        Assert.Throws<BaseLanguageNotTranslatableException>(() => scope.MultiLingual.For(product).Set("en", p => p.Name, "Laptop"));

        Assert.Equal("en", clear.BaseLanguage);
        Assert.Equal("Product", clear.EntityName);
        Assert.True(scope.MultiLingual.Languages.IsBaseLanguage(" en "));
        Assert.Equal("en", scope.MultiLingual.Languages.BaseLanguage);
    }

    [Fact]
    public async Task Set_OnAPropertyThatIsNotTranslatable_IsRefused()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        Assert.Throws<ArgumentException>(() => scope.MultiLingual.For(new Product { Id = 1 }).Set("ar", p => p.Sku, "X"));
        Assert.Throws<ArgumentException>(() => scope.MultiLingual.For(new Product { Id = 1 }).Set("ar", p => p.Name!.Trim(), "X"));
    }

    [Fact]
    public async Task For_AnEntityWithoutTranslations_IsRefused()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = engine.Scope();

        Assert.Throws<ArgumentException>(() => scope.MultiLingual.For(new Plain { Id = 1 }));
    }

    [Fact]
    public async Task MarkAsBaseLanguageValues_KeepsLoadedColumnsFromBecomingATranslation()
    {
        await using var engine = await Engine.StartAsync(b => b.WithBaseLanguage("en"));
        await using var scope = await engine.ScopeInAsync("ar");

        // Loaded for update without localizing: Name still holds the English column's value.
        var product = new Product { Id = 1, Name = "Laptop" };
        scope.MultiLingual.For(product).MarkAsBaseLanguageValues(p => p.Name);
        await scope.MultiLingual.For(product).SaveAsync();

        Assert.Empty(engine.Translations.All);
    }

    // ------------------------------------------------------------------ languages

    [Fact]
    public async Task Languages_CanBeFoundAndAdministered()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = engine.Scope();
        var languages = scope.MultiLingual.Languages;

        Assert.Equal(["en", "ar", "fr"], (await languages.GetAllAsync()).Select(l => l.Code));
        Assert.Equal("en", (await languages.GetDefaultAsync()).Code);
        Assert.Equal("ar", (await languages.FindAsync("AR"))?.Code);
        Assert.Null(await languages.FindAsync("de"));
        await Assert.ThrowsAsync<LanguageNotFoundException>(() => languages.GetAsync("de"));

        await languages.CreateAsync(new NewLanguage { Code = "de", Name = "Deutsch", IsActive = false });
        Assert.NotNull(await languages.FindAsync("de"));        // the cache was refreshed
        Assert.Null(await languages.FindActiveAsync("de"));

        await languages.UpdateAsync("de", new LanguageUpdate { Name = "German" });
        Assert.Equal("de", (await languages.SetDefaultAsync("de")).Code);
        Assert.Equal("de", (await languages.GetDefaultAsync()).Code);

        await languages.SetDefaultAsync("en");
        await languages.DeleteAsync("de");
        Assert.Null(await languages.FindAsync("de"));
    }

    [Fact]
    public async Task Languages_PurgeTranslations_RemovesOneLanguageEverywhere()
    {
        await using var engine = await Engine.StartAsync();
        await SeedLaptopAsync(engine);
        await using var scope = engine.Scope();

        Assert.Equal(2, await scope.MultiLingual.Languages.PurgeTranslationsAsync("fr"));   // Name and Description
        Assert.DoesNotContain(engine.Translations.All, t => t.LanguageCode == "fr");
    }

    [Fact]
    public async Task Languages_InvalidateCache_PicksUpChangesMadeBehindItsBack()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = engine.Scope();

        Assert.Null(await scope.MultiLingual.Languages.FindAsync("it"));
        await engine.Languages.AddAsync(new Language { Code = "it", Name = "Italiano" });   // straight into the store

        Assert.Null(await scope.MultiLingual.Languages.FindAsync("it"));
        scope.MultiLingual.Languages.InvalidateCache();
        Assert.NotNull(await scope.MultiLingual.Languages.FindAsync("it"));
    }

    // ------------------------------------------------------------------ current language

    [Fact]
    public async Task UseLanguageAsync_SetsTheCurrentLanguage_AndReportsARefusal()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = engine.Scope();
        var multiLingual = scope.MultiLingual;

        Assert.False(multiLingual.IsLanguageResolved);
        Assert.Throws<NoLanguageConfiguredException>(() => multiLingual.CurrentLanguage);

        Assert.True(await multiLingual.UseLanguageAsync("fr"));
        Assert.Equal(("fr", "en", true), (multiLingual.CurrentLanguage.Code, multiLingual.DefaultLanguage.Code, multiLingual.WasRequestedLanguageHonoured));

        Assert.False(await multiLingual.UseLanguageAsync("de"));
        Assert.Equal(("en", false), (multiLingual.CurrentLanguage.Code, multiLingual.WasRequestedLanguageHonoured));
    }

    [Fact]
    public async Task ApplyUiCulture_FollowsTheCurrentLanguage()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = engine.Scope();
        var original = CultureInfo.CurrentUICulture;

        try
        {
            Assert.False(scope.MultiLingual.ApplyUiCulture());   // nothing resolved yet

            await scope.MultiLingual.UseLanguageAsync("fr");
            if (LanguageCultures.TryGetCulture("fr", out _))
            {
                Assert.True(scope.MultiLingual.ApplyUiCulture());
                Assert.Equal("fr", CultureInfo.CurrentUICulture.Name);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Product 1: Name in en, ar and fr; Description in en and fr.</summary>
    private static async Task SeedLaptopAsync(Engine engine)
    {
        await using var scope = await engine.ScopeInAsync("en");
        var product = new Product { Id = 1, Name = "Laptop", Description = "Light" };
        scope.MultiLingual.For(product)
            .Set("ar", p => p.Name, "حاسوب")
            .Set("fr", new Dictionary<string, string?> { ["Name"] = "Ordinateur", ["Description"] = "Léger" });
        await scope.MultiLingual.For(product).SaveAsync();
    }
}
