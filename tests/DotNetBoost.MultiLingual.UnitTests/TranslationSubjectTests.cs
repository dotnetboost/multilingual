using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Metadata;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace DotNetBoost.MultiLingual.UnitTests;

/// <summary>
/// <c>For(entityName, entityId)</c> and <c>ForMany(...)</c> — per-language text about a subject
/// whose CLR type the application does not own. The case these exist for: an application that
/// installs a media library wants alt text for a <c>MediaFile</c> it has no <c>[MultiLingual]</c>
/// class for, and must not end up with a second copy of this library's fallback rule to get it.
/// </summary>
public sealed class TranslationSubjectTests
{
    private const string Subject = "MediaFile";

    // ------------------------------------------------------------------ round trip and fallback

    [Fact]
    public async Task TwoLanguagesForOneSubject_ReadBackWithTheDefaultLanguageFallback()
    {
        var file = Guid.NewGuid();

        await using var engine = await Engine.StartAsync();

        await using (var write = await engine.ScopeInAsync("en"))
        {
            await write.MultiLingual.For(Subject, file)
                .Set("en", "AltText", "A red chair")
                .Set("en", "Caption", "Our bestseller")
                .Set("ar", "AltText", "كرسي أحمر")
                .SaveAsync();
        }

        await using (var read = await engine.ScopeInAsync("ar"))
        {
            var values = await read.MultiLingual.For(Subject, file).GetAsync();

            Assert.Equal("كرسي أحمر", values["AltText"]);
            Assert.Equal("Our bestseller", values["Caption"]);   // no Arabic caption: falls back to English
        }

        await using (var read = await engine.ScopeInAsync("en"))
        {
            var stored = await read.MultiLingual.For(Subject, file).GetAllAsync();

            Assert.Equal("A red chair", stored["en"]["AltText"]);
            Assert.Equal("كرسي أحمر", stored["ar"]["AltText"]);
            Assert.False(stored["ar"].ContainsKey("Caption"));
        }
    }

    [Fact]
    public async Task WithTheFallbackDisabled_AMissingValueIsAbsentRatherThanBorrowed()
    {
        await using var engine = await Engine.StartAsync(b => b.DisableDefaultLanguageFallback());

        await using (var write = await engine.ScopeInAsync("en"))
            await write.MultiLingual.For(Subject, 1).Set("en", "AltText", "A red chair").SaveAsync();

        await using (var read = await engine.ScopeInAsync("ar"))
            Assert.Empty(await read.MultiLingual.For(Subject, 1).GetAsync());
    }

    [Fact]
    public async Task AnyPropertyNameIsAccepted_ThereIsNoTypeToValidateItAgainst()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        await scope.MultiLingual.For(Subject, 1).Set("en", "NoSuchPropertyOnAnyClass", "fine").SaveAsync();

        Assert.Equal("fine", (await scope.MultiLingual.For(Subject, 1).GetAsync())["NoSuchPropertyOnAnyClass"]);
    }

    [Fact]
    public async Task ANullValue_RemovesTheStoredRow()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        await scope.MultiLingual.For(Subject, 1).Set("ar", "AltText", "كرسي").SaveAsync();
        await scope.MultiLingual.For(Subject, 1).Set("ar", "AltText", null).SaveAsync();

        Assert.Empty(await scope.MultiLingual.For(Subject, 1).GetAllAsync());
    }

    [Fact]
    public async Task Delete_RemovesEveryLanguage_AndReportsHowMany()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        await scope.MultiLingual.For(Subject, 1)
            .Set("en", "AltText", "A red chair")
            .Set("ar", "AltText", "كرسي أحمر")
            .SaveAsync();

        Assert.Equal(2, await scope.MultiLingual.For(Subject, 1).DeleteAsync());
        Assert.Empty(await scope.MultiLingual.For(Subject, 1).GetAllAsync());
    }

    // ------------------------------------------------------------------ the key format is the library's problem

    [Theory]
    [InlineData(42, "42")]
    [InlineData("42", "42")]
    [InlineData(42L, "42")]
    public async Task AnIdOfAnyKeyType_AddressesTheSameRowsAsItsCanonicalString(object id, string expectedKey)
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        await scope.MultiLingual.For("X", id).Set("en", "AltText", "same row").SaveAsync();

        Assert.Equal(expectedKey, scope.MultiLingual.For("X", id).EntityId);
        Assert.Equal("same row", (await scope.MultiLingual.For("X", "42").GetAsync())["AltText"]);
        Assert.Equal("same row", (await scope.MultiLingual.For("X", 42).GetAsync())["AltText"]);
        Assert.Single(engine.Translations.All);
    }

    [Fact]
    public async Task AGuidId_IsStoredInTheSameFormatEveryProviderUses()
    {
        var id = Guid.NewGuid();

        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        await scope.MultiLingual.For(Subject, id).Set("en", "AltText", "A red chair").SaveAsync();

        Assert.Equal(id.ToString("D"), engine.Translations.All.Single().EntityId);
        Assert.Equal("A red chair", (await scope.MultiLingual.For(Subject, id.ToString("D")).GetAsync())["AltText"]);
    }

    // ------------------------------------------------------------------ ForMany: one query, not one per id

    [Fact]
    public async Task ForMany_Over20Ids_IssuesOneStoreRead()
    {
        var ids = Enumerable.Range(1, 20).ToArray();

        await using var engine = await Engine.StartAsync();

        await using (var write = await engine.ScopeInAsync("en"))
        {
            foreach (var id in ids)
                await write.MultiLingual.For(Subject, id).Set("en", "AltText", $"File {id}").SaveAsync();
        }

        await using (var read = await engine.ScopeInAsync("ar"))
        {
            var before = engine.Translations.GetCalls;

            var text = await read.MultiLingual.ForMany(Subject, ids.Select(id => (object)id).ToArray()).GetAsync();

            Assert.Equal(1, engine.Translations.GetCalls - before);
            Assert.Equal(20, text.Count);
            Assert.Equal("File 7", text["7"]["AltText"]);   // fell back to English, as one entity would
        }
    }

    [Fact]
    public async Task ForMany_IgnoresDuplicateAndUnsetIds_AndOmitsIdsWithNothingStored()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        await scope.MultiLingual.For(Subject, 1).Set("en", "AltText", "File 1").SaveAsync();

        var subjects = scope.MultiLingual.ForMany(Subject, [1, 1, 2, 0]);

        Assert.Equal(["1", "2"], subjects.EntityIds);

        var text = await subjects.GetAsync();
        Assert.True(text.ContainsKey("1"));
        Assert.False(text.ContainsKey("2"));
    }

    [Fact]
    public async Task ForMany_Delete_RemovesEveryIdsTranslations()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        await scope.MultiLingual.For(Subject, 1).Set("en", "AltText", "File 1").SaveAsync();
        await scope.MultiLingual.For(Subject, 2).Set("en", "AltText", "File 2").SaveAsync();

        Assert.Equal(2, await scope.MultiLingual.ForMany(Subject, [1, 2]).DeleteAsync());
        Assert.Empty(engine.Translations.All);
    }

    [Fact]
    public async Task ForMany_WithNoIds_ReadsNothingAtAll()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        var before = engine.Translations.GetCalls;

        Assert.Empty(await scope.MultiLingual.ForMany(Subject, []).GetAsync());
        Assert.Equal(before, engine.Translations.GetCalls);
    }

    // ------------------------------------------------------------------ writes are queued, and atomic

    [Fact]
    public async Task SetQueues_AndSaveWritesEveryLanguageInOneStoreOperation()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        var subject = scope.MultiLingual.For(Subject, 1)
            .Set("en", "AltText", "A red chair")
            .Set("ar", "AltText", "كرسي أحمر")
            .Set("fr", new Dictionary<string, string?> { ["AltText"] = "Chaise rouge", ["Caption"] = "Rouge" });

        Assert.Empty(engine.Translations.All);   // only queued

        var before = engine.Translations.UpsertCalls;
        await subject.SaveAsync();

        Assert.Equal(1, engine.Translations.UpsertCalls - before);
        Assert.Equal(4, engine.Translations.All.Count);
    }

    [Fact]
    public async Task SavingTwice_DoesNotWriteTheSameValuesAgain()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        var subject = scope.MultiLingual.For(Subject, 1).Set("en", "AltText", "A red chair");
        await subject.SaveAsync();

        var after = engine.Translations.UpsertCalls;
        await subject.SaveAsync();

        Assert.Equal(after, engine.Translations.UpsertCalls);
    }

    [Fact]
    public async Task AValueIsStoredUnderTheCanonicalLanguageCode()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        await scope.MultiLingual.For(Subject, 1).Set("FR", "AltText", "Chaise rouge").SaveAsync();

        Assert.Equal("fr", engine.Translations.All.Single().LanguageCode);
    }

    // ------------------------------------------------------------------ mistakes, where they are made

    [Fact]
    public async Task AnUnknownLanguage_ThrowsAtTheSetCall_NotAtTheSave()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        Assert.Throws<LanguageNotFoundException>(() => scope.MultiLingual.For(Subject, 1).Set("de", "AltText", "Roter Stuhl"));
    }

    [Fact]
    public async Task TheBaseLanguageIsAllowed_ASubjectHasNoColumnsOfItsOwn()
    {
        await using var engine = await Engine.StartAsync(b => b.WithBaseLanguage("en"));
        await using var scope = await engine.ScopeInAsync("en");

        await scope.MultiLingual.For(Subject, 1).Set("en", "AltText", "A red chair").SaveAsync();

        Assert.Equal("A red chair", (await scope.MultiLingual.For(Subject, 1).GetAsync())["AltText"]);

        await using var arabic = await engine.ScopeInAsync("ar");
        Assert.Equal("A red chair", (await arabic.MultiLingual.For(Subject, 1).GetAsync())["AltText"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankEntityName_IsRefused(string entityName)
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        Assert.Throws<ArgumentException>(() => scope.MultiLingual.For(entityName, 1));
        Assert.Throws<ArgumentException>(() => scope.MultiLingual.ForMany(entityName, [1]));
    }

    [Fact]
    public async Task AnEntityNameLongerThanTheColumn_IsRefusedWhereItIsWritten()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        var tooLong = new string('x', MultiLingualMetadata.MaxEntityNameLength + 1);

        var error = Assert.Throws<ArgumentException>(() => scope.MultiLingual.For(tooLong, 1));
        Assert.Contains(MultiLingualMetadata.MaxEntityNameLength.ToString(CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData("")]
    public async Task AnUnsetId_IsRefused_ItAddressesNothing(object id)
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        Assert.Throws<ArgumentException>(() => scope.MultiLingual.For(Subject, id));
    }

    [Fact]
    public async Task AKeyLongerThanAKeyColumn_IsRefusedByTheSave()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        var tooLong = new string('k', EntityKey.MaxLength + 1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.MultiLingual.For(Subject, tooLong).Set("en", "AltText", "x").SaveAsync());
    }

    [Fact]
    public async Task ReadingASubjectNobodyHasWritten_IsEmpty_NotAnError()
    {
        await using var engine = await Engine.StartAsync();
        await using var scope = await engine.ScopeInAsync("en");

        Assert.Empty(await scope.MultiLingual.For(Subject, 999).GetAsync());
        Assert.Empty(await scope.MultiLingual.For(Subject, 999).GetAllAsync());
    }

    // ------------------------------------------------------------------ the same rule as the entity path

    [Fact]
    public async Task ASubjectAndAnEntity_ResolveTheSameStoredRowsIdentically()
    {
        // The drift this API exists to prevent: a subject read and an entity read of the same
        // rows, in the same language, must answer the same text.
        await using var engine = await Engine.StartAsync();

        await using (var write = await engine.ScopeInAsync("en"))
        {
            var product = new Product { Id = 1, Name = "Laptop", Description = "Light" };
            write.MultiLingual.For(product).Set("ar", p => p.Name, "حاسوب");
            await write.MultiLingual.For(product).SaveAsync();
        }

        await using (var read = await engine.ScopeInAsync("ar"))
        {
            var entity = new Product { Id = 1 };
            await read.MultiLingual.LocalizeAsync([entity]);

            var subject = await read.MultiLingual.For("Product", 1).GetAsync();

            Assert.Equal(entity.Name, subject["Name"]);
            Assert.Equal(entity.Description, subject["Description"]);
        }
    }

    [Fact]
    public async Task WithNoLanguageResolved_ASubjectReadsInTheDefaultLanguage()
    {
        await using var engine = await Engine.StartAsync();

        await using (var write = await engine.ScopeInAsync("en"))
        {
            await write.MultiLingual.For(Subject, 1)
                .Set("en", "AltText", "A red chair")
                .Set("ar", "AltText", "كرسي أحمر")
                .SaveAsync();
        }

        // A background job that never called UseLanguageAsync.
        await using var scope = engine.Scope();
        Assert.Equal("A red chair", (await scope.MultiLingual.For(Subject, 1).GetAsync())["AltText"]);
    }
}
