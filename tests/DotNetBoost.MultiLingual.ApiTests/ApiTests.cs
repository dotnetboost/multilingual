using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core.Models;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNetBoost.MultiLingual.ApiTests;

public sealed class LanguageResolutionTests
{
    private static async Task<(Probe Probe, HttpResponseMessage Response)> ProbeAsync(TestApp app, string? acceptLanguage)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        if (acceptLanguage is not null)
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);

        var response = await app.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<Probe>())!, response);
    }

    [Theory]
    [InlineData(null, "en", true)]                          // nothing asked: the default, honoured
    [InlineData("ar", "ar", true)]                          // exact
    [InlineData("AR", "ar", true)]                          // case-insensitive
    [InlineData("fr-FR, fr;q=0.9, en;q=0.5", "fr", true)]   // no fr-FR: the bare part of the best entry
    [InlineData("ar;q=0.4, en;q=0.8", "en", true)]          // quality wins over order
    [InlineData("de", "en", false)]                         // unknown: default, not honoured
    [InlineData("de, *;q=0.1", "en", true)]                 // unknown, but "anything" was acceptable
    [InlineData("ar;q=0", "en", true)]                      // q=0 means "anything but this" — the default qualifies
    public async Task AcceptLanguage_IsNegotiated(string? header, string expected, bool honoured)
    {
        await using var app = await TestApp.StartAsync("en", "ar", "fr");

        var (probe, response) = await ProbeAsync(app, header);

        Assert.Equal((expected, "en", honoured), (probe.Current, probe.Default, probe.Honoured));
        Assert.Equal(expected, Assert.Single(response.Content.Headers.ContentLanguage));
        Assert.Contains("Accept-Language", response.Headers.Vary);
    }

    [Fact]
    public async Task InactiveLanguage_IsNeverServed()
    {
        await using var app = await TestApp.StartAsync("en", "ar");
        (await app.Client.PutAsJsonAsync("/api/languages/ar", new { name = "Arabic", isActive = false })).EnsureSuccessStatusCode();

        var (probe, _) = await ProbeAsync(app, "ar");

        Assert.Equal(("en", false), (probe.Current, probe.Honoured));
    }

    [Fact]
    public async Task NoLanguagesYet_ContentEndpointsAnswer409_LanguageEndpointsStillWork()
    {
        await using var app = await TestApp.StartAsync();

        var probe = await app.Client.GetAsync("/probe");
        Assert.Equal(HttpStatusCode.Conflict, probe.StatusCode);
        Assert.Equal("application/problem+json", probe.Content.Headers.ContentType?.MediaType);

        var list = await app.Client.GetFromJsonAsync<List<LanguageResponse>>("/api/languages");
        Assert.Empty(list!);
    }
}

public sealed class LanguageEndpointTests
{
    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    [Fact]
    public async Task List_PutsTheDefaultFirst()
    {
        await using var app = await TestApp.StartAsync("fr", "ar", "en");

        var list = await app.Client.GetFromJsonAsync<List<LanguageResponse>>("/api/languages");

        Assert.Equal(["fr", "ar", "en"], list!.Select(l => l.Code));
        Assert.True(list![0].IsDefault);
    }

    [Fact]
    public async Task Create_Returns201_WithLocation_NormalisedCode_AndInferredDirection()
    {
        await using var app = await TestApp.StartAsync("en");

        var response = await app.Client.PostAsJsonAsync("/api/languages", new { code = "HE", name = "עברית" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/languages/he", response.Headers.Location?.OriginalString);
        var created = await response.Content.ReadFromJsonAsync<LanguageResponse>();
        Assert.Equal(new LanguageResponse("he", "עברית", "rtl", false, true), created);
    }

    [Fact]
    public async Task FirstLanguage_BecomesTheDefault()
    {
        await using var app = await TestApp.StartAsync();

        var created = await (await app.Client.PostAsJsonAsync("/api/languages", new { code = "en", name = "English" }))
            .Content.ReadFromJsonAsync<LanguageResponse>();

        Assert.True(created!.IsDefault);
    }

    [Theory]
    [InlineData("not-a-code", "Nope", null)]
    [InlineData("de", "", null)]
    [InlineData("de", "Deutsch", "sideways")]
    public async Task Create_Invalid_Is400(string code, string name, string? direction)
    {
        await using var app = await TestApp.StartAsync("en");

        var response = await app.Client.PostAsJsonAsync("/api/languages", new { code, name, direction });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid language", (await ProblemAsync(response)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Create_Duplicate_Is409()
    {
        await using var app = await TestApp.StartAsync("en");

        var response = await app.Client.PostAsJsonAsync("/api/languages", new { code = "EN", name = "English" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Get_Unknown_Is404_AsProblemDetails()
    {
        await using var app = await TestApp.StartAsync("en");

        var response = await app.Client.GetAsync("/api/languages/de");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Update_ReplacesFields_AndRefusesToDeactivateTheDefault()
    {
        await using var app = await TestApp.StartAsync("en", "ar");

        var updated = await (await app.Client.PutAsJsonAsync("/api/languages/ar", new { name = "العربية", direction = "ltr" }))
            .Content.ReadFromJsonAsync<LanguageResponse>();
        Assert.Equal(new LanguageResponse("ar", "العربية", "ltr", false, true), updated);

        var refused = await app.Client.PutAsJsonAsync("/api/languages/en", new { name = "English", isActive = false });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task SetDefault_MovesTheDefault()
    {
        await using var app = await TestApp.StartAsync("en", "ar");

        (await app.Client.PostAsync("/api/languages/ar/set-default", null)).EnsureSuccessStatusCode();

        var list = await app.Client.GetFromJsonAsync<List<LanguageResponse>>("/api/languages");
        Assert.Equal(["ar"], list!.Where(l => l.IsDefault).Select(l => l.Code));
    }

    [Fact]
    public async Task Delete_RefusesTheDefault_AndLanguagesWithContent_UntilPurged()
    {
        await using var app = await TestApp.StartAsync("en", "fr");
        await app.Translations.UpsertManyAsync(
            [new Translation { EntityName = "Product", EntityId = "1", PropertyName = "Name", LanguageCode = "fr", Value = "Portable" }]);

        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.DeleteAsync("/api/languages/en")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.DeleteAsync("/api/languages/fr")).StatusCode);

        var purged = await (await app.Client.DeleteAsync("/api/languages/fr/translations"))
            .Content.ReadFromJsonAsync<PurgeTranslationsResponse>();
        Assert.Equal(new PurgeTranslationsResponse("fr", 1), purged);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync("/api/languages/fr")).StatusCode);
    }

    [Fact]
    public async Task CustomPrefix_IsHonoured_IncludingTheLocationHeader()
    {
        await using var app = await TestApp.StartAsync("en");

        // TestApp also maps the endpoints under "/admin/langs/" — trailing slash deliberate.
        var response = await app.Client.PostAsJsonAsync("/admin/langs", new { code = "ar", name = "Arabic" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/admin/langs/ar", response.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/admin/langs/ar")).StatusCode);
    }
}

#if NET10_0_OR_GREATER
public sealed class OpenApiTests
{
    [Fact]
    public async Task AcceptLanguage_IsDocumented_WithLiveCodes_ExceptOnLanguageAgnosticEndpoints()
    {
        await using var app = await TestApp.StartAsync("en", "ar");

        var document = await app.Client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = document.GetProperty("paths");

        static JsonElement? AcceptLanguage(JsonElement operation) =>
            operation.TryGetProperty("parameters", out var parameters)
                ? parameters.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(p => p!.Value.GetProperty("name").GetString() == "Accept-Language")
                : null;

        var probe = AcceptLanguage(paths.GetProperty("/probe").GetProperty("get"));
        Assert.NotNull(probe);
        Assert.Equal(["ar", "en"], probe!.Value.GetProperty("schema").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Order());

        Assert.Null(AcceptLanguage(paths.GetProperty("/agnostic").GetProperty("get")));
        Assert.Null(AcceptLanguage(paths.GetProperty("/api/languages").GetProperty("get")));
    }
}
#endif
