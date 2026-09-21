global using Xunit;
using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetBoost.MultiLingual.ApiTests;

/// <summary>
/// A real ASP.NET Core pipeline over an in-memory transport and the in-memory stores, wired the
/// way an application wires it: <c>UseMultiLingual()</c>, <c>MapLanguageEndpoints()</c>, the
/// exception handler, and a <c>/probe</c> endpoint that reports the resolved language.
/// </summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    public HttpClient Client { get; }

    public InMemoryTranslationStore Translations { get; }

    private TestApp(WebApplication app, InMemoryTranslationStore translations)
    {
        _app         = app;
        Translations = translations;
        Client       = app.GetTestClient();
    }

    /// <param name="languages">Codes to create up front; the first becomes the default.</param>
    public static async Task<TestApp> StartAsync(params string[] languages)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var translations  = new InMemoryTranslationStore();
        var languageStore = new InMemoryLanguageStore();

        builder.Services.AddMultiLingual()
            .UseCustomStores(_ => translations, _ => languageStore)
            .Build();

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<MultiLingualExceptionHandler>();
#if NET10_0_OR_GREATER
        builder.Services.AddOpenApi(o => o.AddAcceptLanguageParameter());
#endif

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseMultiLingual();

        app.MapLanguageEndpoints();
        app.MapLanguageEndpoints("/admin/langs/");
        app.MapGet("/probe", (ILanguageContext language) => new Probe(
            language.Current.Code, language.DefaultLanguage.Code, language.WasRequestedLanguageHonoured));
        app.MapGet("/agnostic", () => "ok").WithMetadata(new LanguageAgnosticAttribute());
#if NET10_0_OR_GREATER
        app.MapOpenApi();
#endif

        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<ILanguageManager>();
            foreach (var code in languages)
                await manager.CreateAsync(new NewLanguage { Code = code, Name = code.ToUpperInvariant() });
        }

        return new TestApp(app, translations);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed record Probe(string Current, string Default, bool Honoured);
