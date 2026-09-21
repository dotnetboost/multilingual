using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.UnitTests;

public sealed class TranslationManagerTests
{
    private static Dictionary<string, string?> Values(string? name, string? description = null)
    {
        var values = new Dictionary<string, string?> { ["Name"] = name };
        if (description is not null)
            values["Description"] = description;
        return values;
    }

    /// <summary>Product 1: en + ar for Name, en only for Description.</summary>
    private static async Task SeedLaptopAsync(Engine engine)
    {
        await using var scope = engine.Scope();
        var product = new Product { Id = 1 };
        scope.Translations.Stage(product, "en", Values("Laptop", "Light"));
        scope.Translations.Stage(product, "ar", Values("حاسوب"));
        await scope.Translations.SaveAsync(product);
    }

    // ------------------------------------------------------------------ read

    [Fact]
    public async Task Hydrate_UsesRequestedLanguage_ThenDefault()
    {
        await using var engine = await Engine.StartAsync();
        await SeedLaptopAsync(engine);
        await using var scope = await engine.ScopeInAsync("ar");

        var product = new Product { Id = 1 };
        await scope.Translations.HydrateAsync([product]);

        Assert.Equal("حاسوب", product.Name);
        Assert.Equal("Light", product.Description);
    }

    [Fact]
    public async Task Hydrate_MissingEverywhere_IsNull()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("fr");

        var product = new Product { Id = 99, Name = "stale" };
        await scope.Translations.HydrateAsync([product]);

        Assert.Null(product.Name);
    }

    [Fact]
    public async Task Hydrate_WithFallbackDisabled_LeavesMissingValuesNull()
    {
        await using var engine = await Engine.StartAsync(b => b.DisableDefaultLanguageFallback());
        await SeedLaptopAsync(engine);
        await using var scope = await engine.ScopeInAsync("ar");

        var product = new Product { Id = 1 };
        await scope.Translations.HydrateAsync([product]);

        Assert.Equal("حاسوب", product.Name);
        Assert.Null(product.Description);
    }

    [Fact]
    public async Task Hydrate_WithoutResolvedLanguage_UsesDefault()
    {
        await using var engine = await Engine.StartAsync();
        await SeedLaptopAsync(engine);
        await using var scope = engine.Scope();

        var product = new Product { Id = 1 };
        await scope.Translations.HydrateAsync([product]);

        Assert.Equal("Laptop", product.Name);
    }

    [Fact]
    public async Task Hydrate_InExplicitLanguage()
    {
        await using var engine = await Engine.StartAsync();
        await SeedLaptopAsync(engine);
        await using var scope = await engine.ScopeInAsync("en");

        var product = new Product { Id = 1 };
        await scope.Translations.HydrateAsync([product], "AR");

        Assert.Equal("حاسوب", product.Name);
        await Assert.ThrowsAsync<LanguageNotFoundException>(() => scope.Translations.HydrateAsync([product], "de"));
    }

    [Fact]
    public async Task Hydrate_FillsEveryInstanceSharingAKey_AndIgnoresOtherTypes()
    {
        await using var engine = await Engine.StartAsync();
        await SeedLaptopAsync(engine);
        await using var scope = await engine.ScopeInAsync("en");

        var a = new Product { Id = 1 };
        var b = new Product { Id = 1 };
        var plain = new Plain { Id = 1, Name = "untouched" };
        await scope.Translations.HydrateAsync([a, b, plain]);

        Assert.Equal("Laptop", a.Name);
        Assert.Equal("Laptop", b.Name);
        Assert.Equal("untouched", plain.Name);
    }

    [Fact]
    public async Task GuidKeys_RoundTrip()
    {
        await using var engine = await Engine.StartAsync();
        var id = Guid.NewGuid();

        await using (var write = await engine.ScopeInAsync("en"))
            await write.Translations.SaveAsync(new Document { Id = id, Title = "Guide" });

        await using var read = await engine.ScopeInAsync("en");
        var document = new Document { Id = id };
        await read.Translations.HydrateAsync([document]);

        Assert.Equal("Guide", document.Title);
        Assert.Equal(id.ToString("D"), Assert.Single(engine.Translations.All).EntityId);
    }

    [Fact]
    public async Task GetAll_GroupsByLanguage()
    {
        await using var engine = await Engine.StartAsync();
        await SeedLaptopAsync(engine);
        await using var scope = engine.Scope();

        var all = await scope.Translations.GetAllAsync(new Product { Id = 1 });

        Assert.Equal("Laptop", all["en"]["Name"]);
        Assert.Equal("Light", all["en"]["Description"]);
        Assert.Equal("حاسوب", all["ar"]["Name"]);
        Assert.Empty(await scope.Translations.GetAllAsync(new Product()));
    }

    // ------------------------------------------------------------------ write

    [Fact]
    public async Task Save_WritesCurrentLanguage_AndSkipsNulls()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("fr");

        await scope.Translations.SaveAsync(new Product { Id = 3, Name = "Souris" });

        var row = Assert.Single(engine.Translations.All);
        Assert.Equal(("Product", "3", "Name", "fr", "Souris"), (row.EntityName, row.EntityId, row.PropertyName, row.LanguageCode, row.Value));
    }

    [Fact]
    public async Task Save_WithoutKey_Throws()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Translations.SaveAsync(new Product { Name = "x" }));
    }

    [Fact]
    public async Task UntouchedFallback_IsNeverPromoted_ButAnEditedOneIs()
    {
        await using var engine = await Engine.StartAsync();
        await SeedLaptopAsync(engine);
        await using var scope = await engine.ScopeInAsync("fr");

        var product = new Product { Id = 1 };
        await scope.Translations.HydrateAsync([product]);
        Assert.Equal("Laptop", product.Name);                     // English, as a fallback

        await scope.Translations.SaveAsync(product);
        Assert.DoesNotContain(engine.Translations.All, t => t.LanguageCode == "fr");

        product.Name = "Ordinateur";
        await scope.Translations.SaveAsync(product);

        var french = Assert.Single(engine.Translations.All, t => t.LanguageCode == "fr");
        Assert.Equal(("Name", "Ordinateur"), (french.PropertyName, french.Value));
    }

    [Fact]
    public async Task UnchangedRealTranslation_IsNotRewritten()
    {
        await using var engine = await Engine.StartAsync();
        await SeedLaptopAsync(engine);
        await using var scope = await engine.ScopeInAsync("ar");

        var product = new Product { Id = 1 };
        await scope.Translations.HydrateAsync([product]);
        var before = engine.Translations.UpsertCalls;

        await scope.Translations.SaveAsync(product);

        Assert.Equal(before, engine.Translations.UpsertCalls);
    }

    [Fact]
    public async Task SecondSave_OfAnUnchangedInstance_WritesNothing()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        var product = new Product { Id = 4, Name = "Headset" };
        await scope.Translations.SaveAsync(product);
        await scope.Translations.SaveAsync(product);

        Assert.Equal(1, engine.Translations.UpsertCalls);
    }

    [Fact]
    public async Task Staged_Values_AreWrittenOnce_WithCanonicalCodes()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = engine.Scope();

        var product = new Product { Id = 5 };
        scope.Translations.Stage(product, "AR", Values("فأرة"));
        scope.Translations.Stage(product, "ar", Values("فأرة لاسلكية"));   // later values win
        await scope.Translations.SaveAsync(product);
        await scope.Translations.SaveAsync(product);                       // consumed: nothing left

        var row = Assert.Single(engine.Translations.All);
        Assert.Equal(("ar", "فأرة لاسلكية"), (row.LanguageCode, row.Value));
        Assert.Equal(1, engine.Translations.UpsertCalls);
    }

    [Fact]
    public async Task Stage_UnknownProperty_Throws()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = engine.Scope();

        Assert.Throws<ArgumentException>(() =>
            scope.Translations.Stage(new Product { Id = 1 }, "en", new Dictionary<string, string?> { ["Sku"] = "x" }));
    }

    [Fact]
    public async Task Write_UnknownLanguage_Throws_AndWritesNothing()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = engine.Scope();

        var product = new Product { Id = 6 };
        scope.Translations.Stage(product, "en", Values("Pen"));
        scope.Translations.Stage(product, "de", Values("Stift"));

        await Assert.ThrowsAsync<LanguageNotFoundException>(() => scope.Translations.SaveAsync(product));
        Assert.Empty(engine.Translations.All);
    }

    [Fact]
    public async Task Delete_RemovesEveryLanguage()
    {
        await using var engine = await Engine.StartAsync();
        await SeedLaptopAsync(engine);
        await using var scope = engine.Scope();

        await scope.Translations.DeleteAsync(new Product { Id = 1 });

        Assert.Empty(engine.Translations.All);
    }

    [Fact]
    public async Task WithStore_WritesElsewhere_ButSharesScopeState()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");
        var other = new InMemoryTranslationStore();

        var product = new Product { Id = 7, Name = "Lamp" };
        scope.Translations.Stage(product, "ar", Values("مصباح"));

        // A provider's save, step by step: capture, write through another store, complete.
        var write = new TranslationWrite("Product", "7", scope.Writer.CaptureValues(product)) { Source = product };
        await scope.Writer.WithStore(other).WriteAsync([write]);

        Assert.Empty(engine.Translations.All);
        Assert.Equal(2, other.All.Count);

        // The staged values were consumed by the sibling, so the original has nothing to write.
        await scope.Translations.SaveAsync(product);
        Assert.Empty(engine.Translations.All);
    }

    [Fact]
    public async Task ManagerAndWriter_AreOneInstancePerScope()
    {
        await using var engine = await Engine.StartAsync();
        await using var first  = engine.Scope();
        await using var second = engine.Scope();

        Assert.Same(first.Translations, first.Writer);
        Assert.NotSame(first.Writer, second.Writer);
    }
}
