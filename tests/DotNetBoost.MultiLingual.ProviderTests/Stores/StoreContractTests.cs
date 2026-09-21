using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;

namespace DotNetBoost.MultiLingual.ProviderTests.Stores;

/// <summary>
/// Contract every <see cref="ITranslationStore"/> must pass. Inherit it and implement
/// <see cref="CreateStoresAsync"/> for a provider — a new provider is done when this suite and
/// <see cref="LanguageStoreContractTests"/> are green against it.
/// </summary>
public abstract class TranslationStoreContractTests
{
    /// <summary>
    /// A fresh, empty translation store and the language store of the same database, holding
    /// no languages. Relational stores enforce a foreign key from translations to languages, so
    /// the tests create their languages first.
    /// </summary>
    protected abstract Task<(ITranslationStore Translations, ILanguageStore Languages)> CreateStoresAsync();

    private async Task<ITranslationStore> CreateAsync()
    {
        var (translations, languages) = await CreateStoresAsync();
        await languages.AddAsync(new Language { Code = "en", Name = "English", IsDefault = true });
        await languages.AddAsync(new Language { Code = "ar", Name = "Arabic" });
        await languages.AddAsync(new Language { Code = "fr", Name = "French" });
        return translations;
    }

    protected static Translation Row(string entityId, string property, string language, string? value, string entityName = "Product") =>
        new() { EntityName = entityName, EntityId = entityId, PropertyName = property, LanguageCode = language, Value = value };

    [Fact]
    public async Task Upsert_ThenGet_ReturnsRows()
    {
        var store = await CreateAsync();
        await store.UpsertManyAsync([Row("1", "Name", "en", "Laptop"), Row("1", "Name", "ar", "حاسوب")]);

        var rows = await store.GetAsync("Product", ["1"], ["en", "ar"]);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r is { LanguageCode: "ar", Value: "حاسوب" });
    }

    [Fact]
    public async Task SyncUpsert_InsertsAndUpdates_LikeTheAsyncOne()
    {
        var store = await CreateAsync();
        store.UpsertMany([Row("1", "Name", "en", "Laptop"), Row("1", "Name", "ar", "حاسوب")]);
        store.UpsertMany([Row("1", "Name", "en", "Notebook")]);

        var rows = await store.GetAsync("Product", ["1"], ["en", "ar"]);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r is { LanguageCode: "en", Value: "Notebook" });
    }

    [Fact]
    public async Task SyncDeleteEntities_RemovesOnlyThoseEntities()
    {
        var store = await CreateAsync();
        await store.UpsertManyAsync([Row("1", "Name", "en", "a"), Row("1", "Name", "ar", "b"), Row("2", "Name", "en", "c")]);

        Assert.Equal(2, store.DeleteEntities("Product", ["1"]));
        Assert.Equal(0, store.DeleteEntities("Product", []));

        var remaining = await store.GetAsync("Product", ["1", "2"], ["en", "ar"]);
        Assert.Equal("2", Assert.Single(remaining).EntityId);
    }

    [Fact]
    public async Task Upsert_ExistingKey_UpdatesValue()
    {
        var store = await CreateAsync();
        await store.UpsertManyAsync([Row("1", "Name", "en", "Laptop")]);
        await store.UpsertManyAsync([Row("1", "Name", "en", "Notebook")]);

        var row = Assert.Single(await store.GetAsync("Product", ["1"], ["en"]));
        Assert.Equal("Notebook", row.Value);
    }

    [Fact]
    public async Task Upsert_RepeatedKeyInOneBatch_LaterRowWins()
    {
        var store = await CreateAsync();
        await store.UpsertManyAsync([Row("1", "Name", "en", "first"), Row("1", "Name", "en", "second")]);

        Assert.Equal("second", Assert.Single(await store.GetAsync("Product", ["1"], ["en"])).Value);
    }

    [Fact]
    public async Task Upsert_NullValue_RemovesTheRow()
    {
        var store = await CreateAsync();
        await store.UpsertManyAsync([Row("1", "Name", "en", "Laptop"), Row("1", "Name", "ar", "حاسوب")]);

        await store.UpsertManyAsync([Row("1", "Name", "en", null), Row("2", "Name", "en", null)]);

        var row = Assert.Single(await store.GetAsync("Product", ["1", "2"], ["en", "ar"]));
        Assert.Equal("ar", row.LanguageCode);
    }

    [Fact]
    public async Task Upsert_NullThenValueInOneBatch_LaterRowWins()
    {
        var store = await CreateAsync();
        await store.UpsertManyAsync([Row("1", "Name", "en", "old")]);

        await store.UpsertManyAsync([Row("1", "Name", "en", null), Row("1", "Name", "en", "new")]);
        Assert.Equal("new", Assert.Single(await store.GetAsync("Product", ["1"], ["en"])).Value);

        await store.UpsertManyAsync([Row("2", "Name", "en", "added"), Row("2", "Name", "en", null)]);
        Assert.Empty(await store.GetAsync("Product", ["2"], ["en"]));
    }

    [Fact]
    public async Task SyncUpsert_NullValue_RemovesTheRow()
    {
        var store = await CreateAsync();
        store.UpsertMany([Row("1", "Name", "en", "Laptop")]);

        store.UpsertMany([Row("1", "Name", "en", null)]);

        Assert.Empty(await store.GetAsync("Product", ["1"], ["en"]));
    }

    [Fact]
    public async Task Upsert_DoesNotTrackOrMutateTheCallersInstances()
    {
        var store = await CreateAsync();
        var row = Row("1", "Name", "en", "Laptop");
        await store.UpsertManyAsync([row]);

        row.Value = "changed after the write";
        await store.UpsertManyAsync([Row("2", "Name", "en", "Mouse")]);

        Assert.Equal("Laptop", Assert.Single(await store.GetAsync("Product", ["1"], ["en"])).Value);
    }

    [Fact]
    public async Task Upsert_LongValues_RoundTrip()
    {
        var store = await CreateAsync();
        var text = new string('x', 20_000);
        await store.UpsertManyAsync([Row("1", "Description", "en", text)]);

        Assert.Equal(text, Assert.Single(await store.GetAsync("Product", ["1"], ["en"])).Value);
    }

    [Fact]
    public async Task Get_FiltersByEntityNameIdsAndLanguages()
    {
        var store = await CreateAsync();
        await store.UpsertManyAsync(
        [
            Row("1", "Name", "en", "Laptop"),
            Row("1", "Name", "fr", "Portable"),
            Row("2", "Name", "en", "Mouse"),
            Row("3", "Name", "en", "Pen"),
            Row("1", "Name", "en", "Books", entityName: "Category")
        ]);

        var rows = await store.GetAsync("Product", ["1", "2"], ["en"]);

        Assert.Equal(["Laptop", "Mouse"], rows.Select(r => r.Value).Order());
    }

    [Fact]
    public async Task Get_WithEmptyFilters_ReturnsNothing()
    {
        var store = await CreateAsync();
        await store.UpsertManyAsync([Row("1", "Name", "en", "Laptop")]);

        Assert.Empty(await store.GetAsync("Product", [], ["en"]));
        Assert.Empty(await store.GetAsync("Product", ["1"], []));
    }

    [Fact]
    public async Task GetAll_ReturnsEveryLanguageForOneEntity()
    {
        var store = await CreateAsync();
        await store.UpsertManyAsync([Row("1", "Name", "en", "Laptop"), Row("1", "Name", "fr", "Portable"), Row("2", "Name", "en", "Mouse")]);

        var rows = await store.GetAllAsync("Product", "1");

        Assert.Equal(["en", "fr"], rows.Select(r => r.LanguageCode).Order());
    }

    [Fact]
    public async Task DeleteEntities_RemovesOnlyThoseEntities()
    {
        var store = await CreateAsync();
        await store.UpsertManyAsync(
        [
            Row("1", "Name", "en", "Laptop"),
            Row("1", "Name", "ar", "حاسوب"),
            Row("2", "Name", "en", "Mouse"),
            Row("1", "Name", "en", "Books", entityName: "Category")
        ]);

        Assert.Equal(2, await store.DeleteEntitiesAsync("Product", ["1"]));

        Assert.Empty(await store.GetAllAsync("Product", "1"));
        Assert.Single(await store.GetAllAsync("Product", "2"));
        Assert.Single(await store.GetAllAsync("Category", "1"));
    }

    [Fact]
    public async Task DeleteEntities_ThenUpsertSameKey_Works()
    {
        var store = await CreateAsync();
        await store.UpsertManyAsync([Row("1", "Name", "en", "Laptop")]);
        await store.DeleteEntitiesAsync("Product", ["1"]);

        await store.UpsertManyAsync([Row("1", "Name", "en", "Reborn")]);

        Assert.Equal("Reborn", Assert.Single(await store.GetAllAsync("Product", "1")).Value);
    }

    [Fact]
    public async Task CountAndDeleteByLanguage()
    {
        var store = await CreateAsync();
        await store.UpsertManyAsync([Row("1", "Name", "en", "Laptop"), Row("1", "Name", "fr", "Portable"), Row("2", "Name", "fr", "Souris")]);

        Assert.Equal(2, await store.CountByLanguageAsync("fr"));
        Assert.Equal(2, await store.DeleteByLanguageAsync("fr"));
        Assert.Equal(0, await store.CountByLanguageAsync("fr"));
        Assert.Equal(1, await store.CountByLanguageAsync("en"));
    }
}

/// <summary>Contract every <see cref="ILanguageStore"/> must pass.</summary>
public abstract class LanguageStoreContractTests
{
    /// <summary>A fresh, empty language store.</summary>
    protected abstract Task<ILanguageStore> CreateStoreAsync();

    private static Language Lang(string code, bool isDefault = false, bool isActive = true) =>
        new() { Code = code, Name = code.ToUpperInvariant(), Direction = TextDirection.Ltr, IsDefault = isDefault, IsActive = isActive };

    [Fact]
    public async Task Add_ThenFind_ReturnsLanguage()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(new Language { Code = "ar", Name = "العربية", Direction = TextDirection.Rtl, IsDefault = true });

        var found = await store.FindAsync("ar");

        Assert.NotNull(found);
        Assert.Equal(("العربية", TextDirection.Rtl, true, true), (found!.Name, found.Direction, found.IsDefault, found.IsActive));
    }

    [Fact]
    public async Task SyncGetAll_ReturnsWhatTheAsyncOneDoes()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Lang("en", isDefault: true));
        await store.AddAsync(Lang("ar"));

        Assert.Equal(
            (await store.GetAllAsync()).Select(l => (l.Code, l.IsDefault)).Order(),
            store.GetAll().Select(l => (l.Code, l.IsDefault)).Order());
    }

    [Fact]
    public async Task Find_Unknown_ReturnsNull()
    {
        var store = await CreateStoreAsync();
        Assert.Null(await store.FindAsync("de"));
    }

    [Fact]
    public async Task Add_AsDefault_ClearsTheOtherDefault()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Lang("en", isDefault: true));
        await store.AddAsync(Lang("ar", isDefault: true));

        var defaults = (await store.GetAllAsync()).Where(l => l.IsDefault).Select(l => l.Code);
        Assert.Equal(["ar"], defaults);
    }

    [Fact]
    public async Task Update_ChangesNameDirectionAndActive_ButNotDefault()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Lang("en", isDefault: true));

        await store.UpdateAsync(new Language { Code = "en", Name = "English", Direction = TextDirection.Rtl, IsActive = false, IsDefault = false });

        var stored = await store.FindAsync("en");
        Assert.Equal(("English", TextDirection.Rtl, false, true), (stored!.Name, stored.Direction, stored.IsActive, stored.IsDefault));
    }

    [Fact]
    public async Task Update_Unknown_Throws()
    {
        var store = await CreateStoreAsync();
        await Assert.ThrowsAsync<LanguageNotFoundException>(() => store.UpdateAsync(Lang("de")));
    }

    [Fact]
    public async Task Delete_RemovesLanguage_AndIgnoresUnknown()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Lang("en", isDefault: true));
        await store.AddAsync(Lang("fr"));

        await store.DeleteAsync("fr");
        await store.DeleteAsync("de");

        Assert.Equal(["en"], (await store.GetAllAsync()).Select(l => l.Code));
    }

    [Fact]
    public async Task SetDefault_LeavesExactlyOneDefault()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Lang("en", isDefault: true));
        await store.AddAsync(Lang("ar"));
        await store.AddAsync(Lang("fr"));

        await store.SetDefaultAsync("fr");

        var defaults = (await store.GetAllAsync()).Where(l => l.IsDefault).Select(l => l.Code);
        Assert.Equal(["fr"], defaults);
    }

    [Fact]
    public async Task SetDefault_Unknown_Throws_AndChangesNothing()
    {
        var store = await CreateStoreAsync();
        await store.AddAsync(Lang("en", isDefault: true));

        await Assert.ThrowsAsync<LanguageNotFoundException>(() => store.SetDefaultAsync("de"));

        Assert.True((await store.FindAsync("en"))!.IsDefault);
    }
}
