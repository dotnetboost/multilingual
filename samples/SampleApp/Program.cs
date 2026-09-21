using System.Text.Json.Serialization;
using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using SampleApp.Data;
using SampleApp.Infrastructure;
using SampleApp.Infrastructure.OpenApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
                       ?? "Data Source=multilingual.db";

// ── MultiLingual ─────────────────────────────────────────────────────────────
// The engine, with EF Core as the provider. AppDbContext holds Translations and Languages.
//
// BaseLanguage = "en" puts English in each entity's OWN table — Products.Name is a real column —
// and every other language in Translations. A missing translation falls back to that column, so
// content is never null once it exists. Three consequences worth seeing in the tour below:
// a product can only be CREATED with Accept-Language: en, English cannot be deleted or
// deactivated, and `SELECT * FROM Products` shows readable names.
//
// Drop the argument to store every language in Translations instead, columns and all.
builder.Services
    .AddMultiLingual(o => o.BaseLanguage = "en")
    .UseEntityFrameworkCore<AppDbContext>()
    .Build();

// The (sp, options) overload, so the interceptors are resolved per request and see its language.
builder.Services.AddDbContext<AppDbContext>((sp, options) => options
    .UseSqlite(connectionString)
    .UseMultiLingual(sp));

builder.Services.AddControllers(options =>
    {
        // Required-ness is declared with explicit [Required] on the DTOs. Without this, a body that
        // fails to bind (say, an unknown field) also reports a confusing "The request field is
        // required." next to the real error.
        options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        // A field the DTO does not declare (e.g. a stale "translations") is a client bug, not
        // something to drop silently: fail binding so [ApiController] answers with a 400.
        options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    });

builder.Services.AddProblemDetails();
// Library failures (unknown language, no language configured, …) first; then the sample's own.
builder.Services.AddExceptionHandler<MultiLingualExceptionHandler>();
builder.Services.AddExceptionHandler<AppExceptionHandler>();

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<ApiDocumentTransformer>();

    // Accept-Language is handled by middleware, not by an action parameter, so it would be
    // invisible in the document — and therefore un-clickable in Scalar — without this.
    options.AddAcceptLanguageParameter();
});

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options
        .WithTitle("DotNetBoost.MultiLingual sample")
        .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient));

    // Land on the docs instead of a 404 at the root.
    app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();
}

app.UseRouting();

// Resolves Accept-Language into the scoped ILanguageContext that the interceptors read.
app.UseMultiLingual();

app.MapControllers();

// Language administration. In a real application: .RequireAuthorization("admin").
app.MapLanguageEndpoints();

// Convenience: report the language the API resolved for this request.
app.MapGet("/api/current-language", (ILanguageContext language) => Results.Ok(new
{
    resolved = language.Current.Code,
    direction = language.Current.Direction == DotNetBoost.MultiLingual.Core.Models.TextDirection.Rtl ? "rtl" : "ltr",
    defaultLanguage = language.DefaultLanguage.Code,
    requestedLanguageHonoured = language.WasRequestedLanguageHonoured
}));

var applyMigrations = builder.Configuration.GetValue("Database:UseMigrations", false);
await DbInitializer.InitializeAsync(app.Services, applyMigrations);

app.Run();

/// <summary>Exposed so an integration test project can drive the app with WebApplicationFactory.</summary>
public partial class Program;
