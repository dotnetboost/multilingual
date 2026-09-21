using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.MultiLingual.UnitTests;

public sealed class LanguageCodesTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData(" ar ", "ar")]
    [InlineData("pt-br", "pt-BR")]
    [InlineData("zh-hant", "zh-HANT")]
    public void Normalize_Canonicalises(string raw, string expected) =>
        Assert.Equal(expected, LanguageCodes.Normalize(raw));

    [Theory]
    [InlineData("")]
    [InlineData("e")]
    [InlineData("english")]
    [InlineData("en_US")]
    [InlineData("not-a-code")]
    [InlineData(null)]
    public void Normalize_RejectsMalformedCodes(string? raw)
    {
        Assert.Throws<InvalidLanguageException>(() => LanguageCodes.Normalize(raw));
        Assert.False(LanguageCodes.TryNormalize(raw, out _));
    }

    [Theory]
    [InlineData("ar", TextDirection.Rtl)]
    [InlineData("he", TextDirection.Rtl)]
    [InlineData("fa", TextDirection.Rtl)]
    [InlineData("en", TextDirection.Ltr)]
    [InlineData("fr", TextDirection.Ltr)]
    public void InferDirection_UsesCultureData(string code, TextDirection expected) =>
        Assert.Equal(expected, LanguageCodes.InferDirection(code));
}

public sealed class LanguageManagerTests
{
    [Fact]
    public async Task FirstLanguage_BecomesActiveDefault_RegardlessOfRequest()
    {
        await using var engine = await Engine.StartAsync(seedLanguages: false);
        await using var scope  = engine.Scope();

        var created = await scope.Get<ILanguageManager>().CreateAsync(
            new NewLanguage { Code = "de", Name = "Deutsch", IsActive = false, IsDefault = false });

        Assert.True(created.IsDefault);
        Assert.True(created.IsActive);
    }

    [Fact]
    public async Task Create_NormalisesCode_AndInfersDirection()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();

        var created = await scope.Get<ILanguageManager>().CreateAsync(new NewLanguage { Code = "HE", Name = " Hebrew " });

        Assert.Equal("he", created.Code);
        Assert.Equal("Hebrew", created.Name);
        Assert.Equal(TextDirection.Rtl, created.Direction);
        Assert.False(created.IsDefault);
    }

    [Fact]
    public async Task Create_DuplicateCode_IsConflict_WhateverTheCase()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();

        await Assert.ThrowsAsync<LanguageConflictException>(() =>
            scope.Get<ILanguageManager>().CreateAsync(new NewLanguage { Code = "EN", Name = "English again" }));
    }

    [Fact]
    public async Task Create_AsDefault_DemotesPreviousDefault()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();
        var manager = scope.Get<ILanguageManager>();

        await manager.CreateAsync(new NewLanguage { Code = "de", Name = "Deutsch", IsDefault = true });

        var defaults = (await manager.GetAllAsync()).Where(l => l.IsDefault).Select(l => l.Code);
        Assert.Equal(["de"], defaults);
    }

    [Fact]
    public async Task Create_InactiveDefault_IsConflict()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();

        await Assert.ThrowsAsync<LanguageConflictException>(() => scope.Get<ILanguageManager>()
            .CreateAsync(new NewLanguage { Code = "de", Name = "Deutsch", IsDefault = true, IsActive = false }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_BlankName_IsInvalid(string name)
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();

        await Assert.ThrowsAsync<InvalidLanguageException>(() =>
            scope.Get<ILanguageManager>().CreateAsync(new NewLanguage { Code = "de", Name = name }));
    }

    [Fact]
    public async Task GetAll_ListsDefaultFirst()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();

        var codes = (await scope.Get<ILanguageManager>().GetAllAsync()).Select(l => l.Code);
        Assert.Equal(["en", "ar", "fr"], codes);
    }

    [Fact]
    public async Task Update_CannotDeactivateTheDefault()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();

        await Assert.ThrowsAsync<LanguageConflictException>(() => scope.Get<ILanguageManager>()
            .UpdateAsync("en", new LanguageUpdate { Name = "English", IsActive = false }));
    }

    [Fact]
    public async Task Update_ReplacesFields_AndRederivesOmittedDirection()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();
        var manager = scope.Get<ILanguageManager>();

        await manager.UpdateAsync("ar", new LanguageUpdate { Name = "العربية", Direction = TextDirection.Ltr });
        var updated = await manager.UpdateAsync("AR", new LanguageUpdate { Name = "العربية", IsActive = false });

        Assert.Equal("العربية", updated.Name);
        Assert.Equal(TextDirection.Rtl, updated.Direction);
        Assert.False(updated.IsActive);
    }

    [Fact]
    public async Task Delete_TheDefault_IsConflict()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();

        await Assert.ThrowsAsync<LanguageConflictException>(() => scope.Get<ILanguageManager>().DeleteAsync("en"));
    }

    [Fact]
    public async Task Delete_WithContent_IsConflict_UntilPurged()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = await engine.ScopeInAsync("fr");
        await scope.Translations.SaveAsync(new Product { Id = 1, Name = "Portable" });
        var manager = scope.Get<ILanguageManager>();

        await Assert.ThrowsAsync<LanguageConflictException>(() => manager.DeleteAsync("fr"));

        Assert.Equal(1, await manager.PurgeTranslationsAsync("fr"));
        await manager.DeleteAsync("fr");

        Assert.DoesNotContain(await manager.GetAllAsync(), l => l.Code == "fr");
    }

    [Fact]
    public async Task SetDefault_MovesTheDefault()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();
        var manager = scope.Get<ILanguageManager>();

        var promoted = await manager.SetDefaultAsync("ar");

        Assert.True(promoted.IsDefault);
        Assert.Equal(["ar"], (await manager.GetAllAsync()).Where(l => l.IsDefault).Select(l => l.Code));
    }

    [Fact]
    public async Task SetDefault_Inactive_IsConflict()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();
        var manager = scope.Get<ILanguageManager>();
        await manager.UpdateAsync("fr", new LanguageUpdate { Name = "French", IsActive = false });

        await Assert.ThrowsAsync<LanguageConflictException>(() => manager.SetDefaultAsync("fr"));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("not-a-code")]
    public async Task UnknownLanguage_IsNotFound(string code)
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();

        await Assert.ThrowsAsync<LanguageNotFoundException>(() => scope.Get<ILanguageManager>().GetAsync(code));
    }

    [Fact]
    public async Task Writes_InvalidateTheCatalog()
    {
        await using var engine = await Engine.StartAsync();
        var catalog = engine.GetSingleton<ILanguageCatalog>();
        Assert.Equal(3, (await catalog.GetAllAsync()).Count);

        await using var scope = engine.Scope();
        await scope.Get<ILanguageManager>().CreateAsync(new NewLanguage { Code = "de", Name = "Deutsch" });

        Assert.Equal(4, (await catalog.GetAllAsync()).Count);
    }
}

public sealed class LanguageCatalogTests
{
    [Fact]
    public async Task ReadsTheStoreOnce_UntilInvalidated()
    {
        await using var engine = await Engine.StartAsync();
        var catalog = engine.GetSingleton<ILanguageCatalog>();
        catalog.Invalidate();
        var before = engine.Languages.GetAllCalls;

        await catalog.GetAllAsync();
        await catalog.GetAllAsync();
        await catalog.FindByCodeAsync("ar");
        Assert.Equal(before + 1, engine.Languages.GetAllCalls);

        catalog.Invalidate();
        await catalog.GetAllAsync();
        Assert.Equal(before + 2, engine.Languages.GetAllCalls);
    }

    [Fact]
    public async Task Expires_AfterTheConfiguredDuration()
    {
        await using var engine = await Engine.StartAsync(b => b.WithLanguageCacheDuration(TimeSpan.FromMinutes(1)));
        var catalog = engine.GetSingleton<ILanguageCatalog>();
        await catalog.GetAllAsync();
        var before = engine.Languages.GetAllCalls;

        engine.Clock.Now += TimeSpan.FromSeconds(59);
        await catalog.GetAllAsync();
        Assert.Equal(before, engine.Languages.GetAllCalls);

        engine.Clock.Now += TimeSpan.FromSeconds(2);
        await catalog.GetAllAsync();
        Assert.Equal(before + 1, engine.Languages.GetAllCalls);
    }

    [Fact]
    public async Task Lookups_AreCaseInsensitive_AndActiveAware()
    {
        await using var engine = await Engine.StartAsync();
        await using (var scope = engine.Scope())
            await scope.Get<ILanguageManager>().UpdateAsync("fr", new LanguageUpdate { Name = "French", IsActive = false });

        var catalog = engine.GetSingleton<ILanguageCatalog>();

        Assert.Equal("ar", (await catalog.FindByCodeAsync("AR"))?.Code);
        Assert.NotNull(await catalog.FindByCodeAsync("fr"));
        Assert.Null(await catalog.FindActiveByCodeAsync("fr"));
        Assert.Equal("en", (await catalog.GetDefaultAsync()).Code);
    }

    [Fact]
    public async Task GetDefault_WithNoLanguages_Throws()
    {
        await using var engine = await Engine.StartAsync(seedLanguages: false);

        await Assert.ThrowsAsync<NoLanguageConfiguredException>(() =>
            engine.GetSingleton<ILanguageCatalog>().GetDefaultAsync());
    }
}

public sealed class LanguageContextTests
{
    [Theory]
    [InlineData("ar", "ar", true)]
    [InlineData("AR", "ar", true)]
    [InlineData("de", "en", false)]
    [InlineData(null, "en", true)]
    public async Task UseAsync_ResolvesOrFallsBackToDefault(string? requested, string expected, bool honoured)
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();
        var context = scope.Get<ILanguageContext>();

        await context.UseAsync(requested);

        Assert.Equal(expected, context.Current.Code);
        Assert.Equal("en", context.DefaultLanguage.Code);
        Assert.Equal(honoured, context.WasRequestedLanguageHonoured);
    }

    [Fact]
    public async Task UseAsync_InactiveLanguage_IsNotHonoured()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();
        await scope.Get<ILanguageManager>().UpdateAsync("fr", new LanguageUpdate { Name = "French", IsActive = false });
        var context = scope.Get<ILanguageContext>();

        Assert.False(await context.UseAsync("fr"));
        Assert.Equal("en", context.Current.Code);
    }

    [Fact]
    public async Task Current_BeforeResolution_Throws()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope  = engine.Scope();
        var context = scope.Get<ILanguageContext>();

        Assert.False(context.IsResolved);
        Assert.Throws<NoLanguageConfiguredException>(() => context.Current);
    }
}

public sealed class BuilderTests
{
    [Fact]
    public void Build_WithoutProvider_Throws()
    {
        var builder = new ServiceCollection().AddMultiLingual();
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("No MultiLingual provider", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondProvider_Throws()
    {
        var builder = new ServiceCollection().AddMultiLingual()
            .UseCustomStores<TestSupport.InMemoryTranslationStore, TestSupport.InMemoryLanguageStore>();

        Assert.Throws<InvalidOperationException>(() =>
            builder.UseCustomStores<TestSupport.InMemoryTranslationStore, TestSupport.InMemoryLanguageStore>());
    }

    [Fact]
    public void Options_AreAppliedThroughTheBuilder()
    {
        var services = new ServiceCollection();
        services.AddMultiLingual()
            .WithLanguageCacheDuration(TimeSpan.FromSeconds(30))
            .DisableDefaultLanguageFallback();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<MultiLingualOptions>();

        Assert.Equal(TimeSpan.FromSeconds(30), options.LanguageCacheDuration);
        Assert.False(options.FallbackToDefaultLanguage);
    }

    [Fact]
    public void CacheDuration_MustBePositive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddMultiLingual().WithLanguageCacheDuration(TimeSpan.Zero));
}
