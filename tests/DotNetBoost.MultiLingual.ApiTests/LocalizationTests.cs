using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.TestSupport;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNetBoost.MultiLingual.ApiTests;

public sealed class LocalizationTests
{
    private static InMemoryFileProvider Files() => new(new Dictionary<string, string>
    {
        ["Resources/en.json"] = """
            {
              "Greeting": "Hello",
              "Failure": "Something went wrong",
              "MultiLingual": { "Problems": { "UnknownLanguage": { "Title": "Unknown language (en)" } } }
            }
            """,
        ["Resources/ar.json"] = """
            {
              "Greeting": "مرحبا",
              "Failure": "حدث خطأ ما",
              "MultiLingual": {
                "Problems": {
                  "UnknownLanguage": { "Title": "لغة غير معروفة", "Detail": "اللغة '{0}' غير موجودة." },
                  "LanguageNotFound": { "Title": "اللغة غير موجودة" }
                }
              }
            }
            """
    });

    private static Task<TestApp> StartAsync(params string[] languages)
    {
        var files = Files();
        return TestApp.StartAsync(b => b.AddJsonLocalization(o => o.FileProvider = files), languages);
    }

    private static async Task<HttpResponseMessage> SendAsync(TestApp app, HttpMethod method, string url, string? acceptLanguage)
    {
        using var request = new HttpRequestMessage(method, url);
        if (acceptLanguage is not null)
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        return await app.Client.SendAsync(request);
    }

    [Theory]
    [InlineData("ar", "مرحبا")]
    [InlineData("en", "Hello")]
    [InlineData("fr", "Hello")]      // a language without a file reads the default's
    [InlineData("de", "Hello")]      // an unknown language resolves to the default, messages too
    public async Task InjectedLocalizer_AnswersInTheResolvedLanguage(string acceptLanguage, string expected)
    {
        await using var app = await StartAsync("en", "ar", "fr");

        var response = await SendAsync(app, HttpMethod.Get, "/messages/Greeting", acceptLanguage);

        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FactoryLocalizer_FollowsTheCultureTheMiddlewareSets()
    {
        await using var app = await StartAsync("en", "ar");

        var response = await SendAsync(app, HttpMethod.Get, "/factory-messages/Greeting", "ar");

        Assert.Equal("مرحبا", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Middleware_SetsTheUICulture_ButNotTheFormattingCulture()
    {
        await using var app = await StartAsync("en", "ar");
        var before = System.Globalization.CultureInfo.CurrentCulture.Name;

        var cultures = await (await SendAsync(app, HttpMethod.Get, "/culture", "ar")).Content.ReadFromJsonAsync<string[]>();

        Assert.Equal("ar", cultures![0]);
        Assert.NotEqual("ar", cultures[1]);
        Assert.Equal(before, System.Globalization.CultureInfo.CurrentCulture.Name);
    }

    [Fact]
    public async Task ApplicationExceptionHandler_AnswersInTheRequestLanguage()
    {
        await using var app = await StartAsync("en", "ar");

        var response = await SendAsync(app, HttpMethod.Get, "/fail/Failure", "ar");

        Assert.Equal((HttpStatusCode)418, response.StatusCode);
        Assert.Equal("حدث خطأ ما", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LibraryProblem_IsLocalized_ThroughTheGlobalExceptionHandler()
    {
        await using var app = await StartAsync("en", "ar");

        var response = await SendAsync(app, HttpMethod.Post, "/notes/xx", "ar");
        var problem  = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("لغة غير معروفة", problem.GetProperty("title").GetString());
        Assert.Equal("اللغة 'xx' غير موجودة.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task LibraryProblem_KeepsTheEnglishDetail_WhenOnlyTheTitleIsTranslated()
    {
        await using var app = await StartAsync("en", "ar");

        var response = await SendAsync(app, HttpMethod.Post, "/notes/xx", "en");
        var problem  = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Unknown language (en)", problem.GetProperty("title").GetString());
        Assert.StartsWith("Language 'xx' does not exist", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibraryProblem_FromTheLanguageEndpoints_IsLocalized()
    {
        await using var app = await StartAsync("en", "ar");

        var response = await SendAsync(app, HttpMethod.Get, "/api/languages/fr", "ar");
        var problem  = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("اللغة غير موجودة", problem.GetProperty("title").GetString());
        Assert.Equal("Language 'fr' was not found.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task WithoutLocalization_ProblemsAreTheBuiltInEnglish()
    {
        await using var app = await TestApp.StartAsync("en", "ar");

        var response = await SendAsync(app, HttpMethod.Post, "/notes/xx", "ar");
        var problem  = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Unknown language", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task AddLocalization_DoesNotReplaceTheJsonLocalizer_WhicheverOrder()
    {
        var files = Files();
        await using var app = await TestApp.StartAsync(b =>
        {
            b.Services.AddLocalization();
            b.AddJsonLocalization(o => o.FileProvider = files);
            b.Services.AddLocalization();
        }, "en", "ar");

        var response = await SendAsync(app, HttpMethod.Get, "/messages/Greeting", "ar");

        Assert.Equal("مرحبا", await response.Content.ReadAsStringAsync());
    }
}
