using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Localization;
using DotNetBoost.MultiLingual.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using System.Globalization;

namespace DotNetBoost.MultiLingual.UnitTests;

/// <summary>Stands in for an application's shared-resource marker class.</summary>
public sealed class Messages;

public sealed class JsonLocalizationTests
{
    private const string English = """
        {
          // Comments and trailing commas are allowed: these files are edited by hand.
          "Product": {
            "Created": "Product {0} was created.",
            "Deleted": "Product deleted.",
          },
          "Flat.Key": "flat",
          "OnlyInEnglish": "english only"
        }
        """;

    private const string Arabic = """
        { "Product": { "Created": "تم إنشاء المنتج {0}.", "Deleted": "تم حذف المنتج." }, "Flat.Key": "مسطح" }
        """;

    private static InMemoryFileProvider Files(params (string Path, string Content)[] files) =>
        new(files.ToDictionary(f => f.Path, f => f.Content));

    private static InMemoryFileProvider DefaultFiles() =>
        Files(("Resources/en.json", English), ("Resources/ar.json", Arabic));

    private static Task<Engine> StartAsync(InMemoryFileProvider files, Action<MultiLingualBuilder>? configure = null) =>
        Engine.StartAsync(b =>
        {
            configure?.Invoke(b);
            b.AddJsonLocalization(o => o.FileProvider = files);
        });

    private static async Task<IStringLocalizer<Messages>> LocalizerInAsync(Engine engine, string language)
    {
        var scope = await engine.ScopeInAsync(language);
        return scope.Get<IStringLocalizer<Messages>>();
    }

    [Fact]
    public async Task Message_IsReadInTheScopesLanguage()
    {
        await using var engine = await StartAsync(DefaultFiles());

        var arabic  = await LocalizerInAsync(engine, "ar");
        var english = await LocalizerInAsync(engine, "en");

        Assert.Equal("تم حذف المنتج.", arabic["Product.Deleted"]);
        Assert.Equal("Product deleted.", english["Product.Deleted"]);
    }

    [Fact]
    public async Task NestedAndDottedKeys_AreTheSame()
    {
        await using var engine = await StartAsync(DefaultFiles());
        var localizer = await LocalizerInAsync(engine, "en");

        Assert.Equal("flat", localizer["Flat.Key"]);
        Assert.Equal("Product deleted.", localizer["Product.Deleted"]);
    }

    [Fact]
    public async Task Arguments_AreFormattedIntoTheMessage()
    {
        await using var engine = await StartAsync(DefaultFiles());
        var localizer = await LocalizerInAsync(engine, "ar");

        Assert.Equal("تم إنشاء المنتج SKU-1.", localizer["Product.Created", "SKU-1"]);
    }

    [Fact]
    public async Task MissingInTheRequestedLanguage_FallsBackToTheDefault()
    {
        await using var engine = await StartAsync(DefaultFiles());
        var localizer = await LocalizerInAsync(engine, "ar");

        var message = localizer["OnlyInEnglish"];

        Assert.Equal("english only", message.Value);
        Assert.False(message.ResourceNotFound);
        Assert.Equal("Resources/en.json", message.SearchedLocation);
    }

    [Fact]
    public async Task LanguageWithoutAFile_ReadsTheDefault()
    {
        await using var engine = await StartAsync(DefaultFiles());
        var localizer = await LocalizerInAsync(engine, "fr");

        Assert.Equal("Product deleted.", localizer["Product.Deleted"]);
    }

    [Fact]
    public async Task MissingEverywhere_ReturnsTheKey_MarkedNotFound()
    {
        await using var engine = await StartAsync(DefaultFiles());
        var localizer = await LocalizerInAsync(engine, "ar");

        var message = localizer["Nope", 42];

        Assert.True(message.ResourceNotFound);
        Assert.Equal("Nope", message.Value);
        Assert.Equal("Resources/ar.json, Resources/en.json", message.SearchedLocation);
    }

    [Fact]
    public async Task DisabledDefaultFallback_IsHonoured()
    {
        await using var engine = await StartAsync(DefaultFiles(), b => b.DisableDefaultLanguageFallback());
        var localizer = await LocalizerInAsync(engine, "ar");

        Assert.True(localizer["OnlyInEnglish"].ResourceNotFound);
    }

    [Fact]
    public void RegionalLanguage_FallsBackToItsParentBeforeTheDefault()
    {
        var chain = new LanguageChain(new MultiLingualOptions(), new EmptyCatalog());

        Assert.Equal(["ar-EG", "ar", "en"], chain.Build("ar-EG", "en"));
        Assert.Equal(["en"], chain.Build(null, "en"));
        Assert.Equal(["en-US", "en"], chain.Build("en-US", "en"));
    }

    [Fact]
    public void BaseLanguage_IsTheLastResort_EvenWithoutDefaultFallback()
    {
        var options = new MultiLingualOptions { BaseLanguage = "en", FallbackToDefaultLanguage = false };
        var chain   = new LanguageChain(options, new EmptyCatalog());

        Assert.Equal(["ar", "en"], chain.Build("ar", "fr"));
    }

    [Fact]
    public async Task FileNames_MatchCodesCaseInsensitively()
    {
        await using var engine = await StartAsync(Files(("Resources/en.json", English), ("Resources/AR.json", Arabic)));
        var localizer = await LocalizerInAsync(engine, "ar");

        Assert.Equal("مسطح", localizer["Flat.Key"]);
    }

    [Fact]
    public async Task EditedFile_IsPickedUpWithoutARestart()
    {
        var files = DefaultFiles();
        await using var engine = await StartAsync(files);
        var localizer = await LocalizerInAsync(engine, "ar");
        Assert.Equal("مسطح", localizer["Flat.Key"]);

        files.Set("Resources/ar.json", """{ "Flat.Key": "جديد" }""");

        Assert.Equal("جديد", localizer["Flat.Key"]);
    }

    [Fact]
    public async Task GetAllStrings_WithParents_MergesTheChain_FirstWins()
    {
        await using var engine = await StartAsync(DefaultFiles());
        var localizer = await LocalizerInAsync(engine, "ar");

        var own = localizer.GetAllStrings(includeParentCultures: false).ToDictionary(s => s.Name, s => s.Value);
        var all = localizer.GetAllStrings(includeParentCultures: true).ToDictionary(s => s.Name, s => s.Value);

        Assert.Equal(3, own.Count);
        Assert.Equal(4, all.Count);
        Assert.Equal("مسطح", all["Flat.Key"]);
        Assert.Equal("english only", all["OnlyInEnglish"]);
    }

    [Theory]
    [InlineData("""{ "A": 1 }""", "'A' must be a string or an object of strings, not Number")]
    [InlineData("""{ "A": { "B": "x" }, "A.B": "y" }""", "'A.B' is defined more than once")]
    [InlineData("""[ "x" ]""", "the top level must be a JSON object")]
    [InlineData("""{ "A": """, "not valid JSON")]
    public async Task MalformedFile_FailsStartup_NamingTheFileAndTheProblem(string content, string expected)
    {
        await using var engine = await StartAsync(Files(("Resources/en.json", English), ("Resources/ar.json", content)));
        var validation = engine.GetSingleton<IEnumerable<IHostedService>>().OfType<JsonLocalizationValidationHostedService>().Single();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => validation.StartAsync(CancellationToken.None));

        Assert.Contains("Resources/ar.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FactoryLocalizer_FollowsTheAmbientUICulture()
    {
        await using var engine = await StartAsync(DefaultFiles());
        var factory = engine.GetSingleton<IStringLocalizerFactory>();
        var localizer = factory.Create(typeof(Messages));

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar");
            Assert.Equal("مسطح", localizer["Flat.Key"]);

            // A culture with no language behind it still falls back to the catalog's default.
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");
            Assert.Equal("flat", localizer["Flat.Key"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public async Task LocalizerFromTheRootProvider_IsRejected_NotSilentlyLanguageless()
    {
        await using var engine = await StartAsync(DefaultFiles());

        Assert.Throws<InvalidOperationException>(() => engine.GetSingleton<IStringLocalizer<Messages>>());
    }

    [Fact]
    public async Task ApplyUiCulture_SetsTheAmbientCultureForTheCaller()
    {
        await using var engine = await StartAsync(DefaultFiles());
        await using var scope = await engine.ScopeInAsync("ar");
        var previous = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);

        try
        {
            Assert.True(scope.Get<ILanguageContext>().ApplyUiCulture());

            Assert.Equal("ar", CultureInfo.CurrentUICulture.Name);
            Assert.Equal(previous.CurrentCulture, CultureInfo.CurrentCulture);
        }
        finally
        {
            (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture) = previous;
        }
    }

    private sealed class EmptyCatalog : ILanguageCatalog
    {
        public Task<IReadOnlyList<Core.Models.LanguageInfo>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Core.Models.LanguageInfo>>([]);

        public Task<Core.Models.LanguageInfo> GetDefaultAsync(CancellationToken ct = default) => throw new NoLanguageConfiguredException();

        public Task<Core.Models.LanguageInfo?> FindByCodeAsync(string? code, CancellationToken ct = default) =>
            Task.FromResult<Core.Models.LanguageInfo?>(null);

        public Task<Core.Models.LanguageInfo?> FindActiveByCodeAsync(string? code, CancellationToken ct = default) =>
            Task.FromResult<Core.Models.LanguageInfo?>(null);

        public void Invalidate() { }
    }
}
