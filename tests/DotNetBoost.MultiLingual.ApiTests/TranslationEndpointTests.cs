using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using DotNetBoost.MultiLingual.Localization;
using DotNetBoost.MultiLingual.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNetBoost.MultiLingual.ApiTests;

/// <summary>
/// <c>MapTranslationEndpoints()</c> — writing translations over HTTP without every consumer
/// hand-writing the same CRUD, and without an open <c>{entityName}</c> route letting a caller
/// write rows for any entity in the system.
/// </summary>
public sealed class TranslationEndpointTests
{
    private const string Subject = "/api/translations/MediaFile/11111111-1111-1111-1111-111111111111";

    private static readonly Guid FileId = new("11111111-1111-1111-1111-111111111111");

    // ------------------------------------------------------------------ round trip, per verb

    [Fact]
    public async Task Put_ThenGet_ReturnsEveryStoredLanguage()
    {
        await using var app = await TestApp.StartAsync("en", "ar");

        var write = await app.Client.PutAsJsonAsync(Subject, new
        {
            translations = new Dictionary<string, Dictionary<string, string?>>
            {
                ["en"] = new() { ["AltText"] = "A red chair", ["Caption"] = "Our bestseller" },
                ["ar"] = new() { ["AltText"] = "كرسي أحمر" }
            }
        });

        Assert.Equal(HttpStatusCode.NoContent, write.StatusCode);

        var stored = await app.Client.GetFromJsonAsync<TranslationsResponse>(Subject);

        Assert.Equal("MediaFile", stored!.EntityName);
        Assert.Equal(FileId.ToString("D"), stored.EntityId);
        Assert.Equal("A red chair", stored.Translations["en"]["AltText"]);
        Assert.Equal("كرسي أحمر", stored.Translations["ar"]["AltText"]);
        Assert.False(stored.Translations["ar"].ContainsKey("Caption"));
    }

    [Fact]
    public async Task GetResolved_ServesTheRequestLanguage_WithTheDefaultLanguageFallback()
    {
        await using var app = await TestApp.StartAsync("en", "ar");
        await SeedAsync(app);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Subject}/resolved");
        request.Headers.TryAddWithoutValidation("Accept-Language", "ar");

        var response = await app.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var resolved = await response.Content.ReadFromJsonAsync<ResolvedTranslationsResponse>();

        Assert.Equal("ar", resolved!.LanguageCode);
        Assert.Equal("كرسي أحمر", resolved.Values["AltText"]);
        Assert.Equal("Our bestseller", resolved.Values["Caption"]);   // no Arabic caption: English
    }

    [Fact]
    public async Task PostResolve_AnswersManyIdsAtOnce_FromOneQuery()
    {
        await using var app = await TestApp.StartAsync("en", "ar");

        foreach (var id in Enumerable.Range(1, 20))
        {
            (await app.Client.PutAsJsonAsync($"/api/translations/MediaFile/{id}", new
            {
                translations = new Dictionary<string, Dictionary<string, string?>>
                {
                    ["en"] = new() { ["AltText"] = $"File {id}" }
                }
            })).EnsureSuccessStatusCode();
        }

        var before = app.Translations.GetCalls;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/translations/MediaFile/resolve")
        {
            Content = JsonContent.Create(new { entityIds = Enumerable.Range(1, 20).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray() })
        };
        request.Headers.TryAddWithoutValidation("Accept-Language", "ar");

        var response = await app.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var batch = await response.Content.ReadFromJsonAsync<ResolvedTranslationsBatchResponse>();

        Assert.Equal(1, app.Translations.GetCalls - before);
        Assert.Equal("ar", batch!.LanguageCode);
        Assert.Equal(20, batch.Subjects.Count);
        Assert.Equal("File 7", batch.Subjects["7"]["AltText"]);
    }

    [Fact]
    public async Task Delete_RemovesEveryLanguage_AndReportsHowMany()
    {
        await using var app = await TestApp.StartAsync("en", "ar");
        await SeedAsync(app);

        var response = await app.Client.DeleteAsync(Subject);
        response.EnsureSuccessStatusCode();

        var deleted = await response.Content.ReadFromJsonAsync<DeleteTranslationsResponse>();
        Assert.Equal(3, deleted!.DeletedTranslations);

        var stored = await app.Client.GetFromJsonAsync<TranslationsResponse>(Subject);
        Assert.Empty(stored!.Translations);
    }

    [Fact]
    public async Task Put_WritesEveryLanguageInTheBatchAsOneStoreOperation()
    {
        await using var app = await TestApp.StartAsync("en", "ar", "fr");

        var before = app.Translations.UpsertCalls;

        (await app.Client.PutAsJsonAsync(Subject, new
        {
            translations = new Dictionary<string, Dictionary<string, string?>>
            {
                ["en"] = new() { ["AltText"] = "A red chair" },
                ["ar"] = new() { ["AltText"] = "كرسي أحمر" },
                ["fr"] = new() { ["AltText"] = "Chaise rouge" }
            }
        })).EnsureSuccessStatusCode();

        Assert.Equal(1, app.Translations.UpsertCalls - before);
        Assert.Equal(3, app.Translations.All.Count);
    }

    [Fact]
    public async Task Put_WithANullValue_RemovesThatStoredRow()
    {
        await using var app = await TestApp.StartAsync("en", "ar");
        await SeedAsync(app);

        (await app.Client.PutAsJsonAsync(Subject, new
        {
            translations = new Dictionary<string, Dictionary<string, string?>>
            {
                ["ar"] = new() { ["AltText"] = null }
            }
        })).EnsureSuccessStatusCode();

        var stored = await app.Client.GetFromJsonAsync<TranslationsResponse>(Subject);

        Assert.False(stored!.Translations.ContainsKey("ar"));
        Assert.Equal("A red chair", stored.Translations["en"]["AltText"]);
    }

    [Fact]
    public async Task AnEntityExposedByType_IsAddressedByTheNameItsRowsAreFiledUnder()
    {
        await using var app = await TestApp.StartAsync("en", "ar");

        var path = $"/api/translations/{MultiLingualMetadata.EntityNameFor(typeof(Note))}/1";

        (await app.Client.PutAsJsonAsync(path, new
        {
            translations = new Dictionary<string, Dictionary<string, string?>> { ["ar"] = new() { ["Text"] = "مرحبا" } }
        })).EnsureSuccessStatusCode();

        // Written under the same name the entity path uses, so the engine reads it back for the
        // entity itself — not into a parallel set of rows nothing else can find.
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILanguageContext>().UseAsync("ar");

        var note = new Note { Id = 1 };
        await scope.ServiceProvider.GetRequiredService<IMultiLingualManager>().LocalizeAsync([note]);

        Assert.Equal("مرحبا", note.Text);
    }

    // ------------------------------------------------------------------ the opt-in gate

    [Theory]
    [InlineData("GET", "/api/translations/Secret/1")]
    [InlineData("GET", "/api/translations/Secret/1/resolved")]
    [InlineData("PUT", "/api/translations/Secret/1")]
    [InlineData("POST", "/api/translations/Secret/resolve")]
    [InlineData("DELETE", "/api/translations/Secret/1")]
    public async Task AnUnexposedName_Answers404OnEveryVerb(string method, string path)
    {
        await using var app = await TestApp.StartAsync("en");

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "PUT" or "POST")
            request.Content = JsonContent.Create(new { translations = new Dictionary<string, Dictionary<string, string?>>(), entityIds = Array.Empty<string>() });

        var response = await app.Client.SendAsync(request);

        // 404, not 403: a 403 would confirm which entity names exist.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnUnexposedName_IsNotWritten_EvenWithAValidBody()
    {
        await using var app = await TestApp.StartAsync("en");

        await app.Client.PutAsJsonAsync("/api/translations/Secret/1", new
        {
            translations = new Dictionary<string, Dictionary<string, string?>> { ["en"] = new() { ["Name"] = "leaked" } }
        });

        Assert.Empty(app.Translations.All);
    }

    [Fact]
    public async Task WithNothingExposed_NothingIsMappedAtAll()
    {
        await using var app = await BareAppAsync(expose: false);

        var response = await app.Client.GetAsync("/api/translations/MediaFile/1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExposeOverHttp_AddsToOneListAcrossCalls()
    {
        await using var app = await BareAppAsync(expose: true);

        var exposed = app.Services.GetRequiredService<ExposedTranslationEntities>();

        Assert.Equal(["Alpha", "Beta"], exposed.Names);
        Assert.True(exposed.IsExposed("alpha"));   // names match the way a database collation does
        Assert.False(exposed.IsExposed("Gamma"));
    }

    // ------------------------------------------------------------------ problem details

    [Fact]
    public async Task AnUnknownLanguage_IsA400ProblemInTheRequestLanguage()
    {
        await using var app = await TestApp.StartAsync(
            b => b.AddJsonLocalization(o => o.FileProvider = Resources()), "en", "ar");

        using var request = new HttpRequestMessage(HttpMethod.Put, Subject)
        {
            Content = JsonContent.Create(new
            {
                translations = new Dictionary<string, Dictionary<string, string?>> { ["de"] = new() { ["AltText"] = "Roter Stuhl" } }
            })
        };
        request.Headers.TryAddWithoutValidation("Accept-Language", "ar");

        var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("لغة غير موجودة", problem.GetProperty("title").GetString());
        Assert.Equal(Subject, problem.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task ABlankId_IsA400Problem_NotARowFiledUnderWhitespace()
    {
        await using var app = await TestApp.StartAsync("en");

        // "%20" matches the route but addresses no subject.
        var response = await app.Client.PutAsJsonAsync("/api/translations/MediaFile/%20", new
        {
            translations = new Dictionary<string, Dictionary<string, string?>> { ["en"] = new() { ["AltText"] = "x" } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Translations.All);
    }

    [Fact]
    public async Task AnIdOfZero_IsTheStringKey_Zero_NotAnUnsetKey()
    {
        // Over the wire an id is a string, and "0" is a perfectly good string key. The unset-key
        // rule belongs to the typed API, where the key's CLR type is known.
        await using var app = await TestApp.StartAsync("en");

        (await app.Client.PutAsJsonAsync("/api/translations/MediaFile/0", new
        {
            translations = new Dictionary<string, Dictionary<string, string?>> { ["en"] = new() { ["AltText"] = "x" } }
        })).EnsureSuccessStatusCode();

        Assert.Equal("0", app.Translations.All.Single().EntityId);
    }

    [Fact]
    public async Task ReadingASubjectNobodyHasWritten_IsAnEmptyBody_NotA404()
    {
        await using var app = await TestApp.StartAsync("en");

        var stored = await app.Client.GetFromJsonAsync<TranslationsResponse>("/api/translations/MediaFile/999");
        Assert.Empty(stored!.Translations);

        var resolved = await app.Client.GetFromJsonAsync<ResolvedTranslationsResponse>("/api/translations/MediaFile/999/resolved");
        Assert.Empty(resolved!.Values);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task SeedAsync(TestApp app) =>
        (await app.Client.PutAsJsonAsync(Subject, new
        {
            translations = new Dictionary<string, Dictionary<string, string?>>
            {
                ["en"] = new() { ["AltText"] = "A red chair", ["Caption"] = "Our bestseller" },
                ["ar"] = new() { ["AltText"] = "كرسي أحمر" }
            }
        })).EnsureSuccessStatusCode();

    private static InMemoryFileProvider Resources() => new(new Dictionary<string, string>
    {
        ["Resources/ar.json"] = """{ "MultiLingual.Problems.LanguageNotFound.Title": "لغة غير موجودة" }"""
    });

    /// <summary>
    /// An app that maps the endpoints with its own exposure list — or none at all, which is the
    /// "nothing is mapped" case.
    /// </summary>
    private static async Task<BareApp> BareAppAsync(bool expose)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var multiLingual = builder.Services.AddMultiLingual()
            .UseCustomStores(_ => new InMemoryTranslationStore(), _ => new InMemoryLanguageStore());

        if (expose)
        {
            multiLingual
                .ExposeOverHttp(e => e.Allow("Alpha"))
                .ExposeOverHttp(e => e.Allow("Beta").Allow("alpha"));
        }

        multiLingual.Build();

        var app = builder.Build();
        app.MapTranslationEndpoints();
        await app.StartAsync();

        return new BareApp(app);
    }

    private sealed class BareApp(WebApplication app) : IAsyncDisposable
    {
        public HttpClient Client { get; } = app.GetTestClient();

        public IServiceProvider Services => app.Services;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
