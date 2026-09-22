global using Xunit;
using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using System.Globalization;

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
    public static Task<TestApp> StartAsync(params string[] languages) => StartAsync(configure: null, languages);

    /// <param name="configure">Further engine configuration, such as <c>AddJsonLocalization()</c>.</param>
    /// <param name="languages">Codes to create up front; the first becomes the default.</param>
    public static async Task<TestApp> StartAsync(Action<MultiLingualBuilder>? configure, params string[] languages)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var translations  = new InMemoryTranslationStore();
        var languageStore = new InMemoryLanguageStore();

        var multiLingual = builder.Services.AddMultiLingual()
            .UseCustomStores(_ => translations, _ => languageStore);
        configure?.Invoke(multiLingual);
        multiLingual.Build();

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<MultiLingualExceptionHandler>();
        builder.Services.AddExceptionHandler<LocalizedFailureHandler>();
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

        // Localized messages: injected, through the factory as framework code does, and from an
        // exception handler — which runs outside UseMultiLingual().
        app.MapGet("/messages/{key}", ([FromServices] IStringLocalizer<Note> localizer, string key) => localizer[key].Value);
        app.MapGet("/factory-messages/{key}", ([FromServices] IStringLocalizerFactory factory, string key) =>
            factory.Create(typeof(Note))[key].Value);
        app.MapGet("/culture", () => new[] { CultureInfo.CurrentUICulture.Name, CultureInfo.CurrentCulture.Name });
        app.MapGet("/fail/{key}", string (string key) => throw new LocalizedFailure(key));

        // Content written in a language given by the caller — as an endpoint accepting a
        // "language" field would.
        app.MapPost("/notes/{language}", async (string language, ITranslationManager translations) =>
        {
            var note = new Note { Id = 1 };
            translations.Stage(note, language, new Dictionary<string, string?> { ["Text"] = "hello" });
            await translations.SaveAsync(note);
            return Results.NoContent();
        });
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

/// <summary>An application failure whose message is a localization key.</summary>
internal sealed class LocalizedFailure(string key) : Exception(key)
{
    public string Key { get; } = key;
}

/// <summary>
/// An application's own exception handler. Handlers are singletons, so it takes the localizer
/// from the request's services rather than its constructor.
/// </summary>
internal sealed class LocalizedFailureHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not LocalizedFailure failure)
            return false;

        var localizer = httpContext.RequestServices.GetRequiredService<IStringLocalizer<Note>>();
        httpContext.Response.StatusCode = StatusCodes.Status418ImATeapot;
        await httpContext.Response.WriteAsync(localizer[failure.Key], cancellationToken);
        return true;
    }
}

/// <summary>Public, and valid, because Build() validates every public decorated type it can see.</summary>
public sealed class Note
{
    public int Id { get; set; }

    [DotNetBoost.MultiLingual.Core.Attributes.MultiLingual] public string? Text { get; set; }
}
