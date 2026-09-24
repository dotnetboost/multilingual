using System.Text.Json.Serialization;
using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core.Interfaces;
using MongoDB.Driver;
using SampleApp.Infrastructure;
using SampleApp.MongoDb.Data;
using SampleApp.MongoDb.Domain;
using SampleApp.MongoDb.Infrastructure.OpenApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry, health checks, service discovery and resilient HttpClients — as in the EF Core
// sample; inert standalone, and what feeds the Aspire dashboard under the AppHost.
builder.AddServiceDefaults();

// "multilingualmongodb" is the resource name from aspire/DotNetBoost.MultiLingual.AppHost/AppHost.cs. Run this
// sample on its own and you supply it yourself:
//   ConnectionStrings__multilingualmongodb="mongodb://localhost:27017/multilingual"
var connectionString = builder.Configuration.GetConnectionString("multilingualmongodb")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:multilingualmongodb is not set. Start the sample through the AppHost "
        + "(dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost), or point it at a MongoDB "
        + "database — see samples/SampleApp.MongoDb/README.md.");

// ── The driver ───────────────────────────────────────────────────────────────
// One client for the process, as the driver expects: it owns the connection pool. The collections are
// singletons too — IMongoCollection is thread-safe and stateless, so nothing here is per-request.
// (The library's own stores are scoped, so a session put in the request's scope would reach them.)
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
builder.Services.AddSingleton(sp =>
{
    var url = MongoUrl.Create(connectionString);
    return sp.GetRequiredService<IMongoClient>().GetDatabase(url.DatabaseName ?? "multilingual");
});
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<Product>("products"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<Category>("categories"));
builder.Services.AddSingleton<IdSequence>();

// ── MultiLingual ─────────────────────────────────────────────────────────────
// The EF Core sample's chain with ONE line changed: UseMongoDb() instead of
// UseEntityFrameworkCore<AppDbContext>(). Everything else — the base language, the JSON localizer,
// Accept-Language resolution, the language endpoints — is provider-neutral and identical.
//
// BaseLanguage = "en" keeps English in each document's OWN fields and every other language in the
// translations collection. A missing translation falls back to that field, so content is never null
// once it exists. Three consequences, all visible through the API: a product can only be CREATED
// with Accept-Language: en, English cannot be deleted or deactivated, and db.products.find() reads
// as English.
//
// Drop the argument and translatable properties are unmapped from the class map entirely: every
// language, English included, becomes a document in the translations collection. Nothing else in
// this file or in the controllers changes.
//
// UseMongoDb() also creates the translations indexes when the host starts — there are no migrations
// to hang that off. MongoDbOptions configures the collection names and can turn it off.
builder.Services
    .AddMultiLingual(o => o.BaseLanguage = "en")
    .UseMongoDb()
    .AddJsonLocalization()
    .Build();

// Readiness, as AddDbContextCheck<AppDbContext>() is for the EF Core sample.
builder.Services.AddHealthChecks()
    .AddCheck<MongoHealthCheck>("mongodb");

// The driver emits no ActivitySource of its own, unlike Npgsql. For the equivalent spans on the
// Aspire dashboard, add MongoDB.Driver.Core.Extensions.DiagnosticSources and hook its subscriber in
// the MongoClientSettings' ClusterConfigurator.

builder.Services.AddControllers(options =>
    {
        options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    });

builder.Services.AddProblemDetails();
// Library failures (unknown language, base language required, …) first; then the sample's own.
builder.Services.AddExceptionHandler<MultiLingualExceptionHandler>();
builder.Services.AddExceptionHandler<AppExceptionHandler>();

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<ApiDocumentTransformer>();
    options.AddAcceptLanguageParameter();
});

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options
        .WithTitle("DotNetBoost.MultiLingual sample (MongoDB)")
        .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient));

    app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();
}

app.UseRouting();

// Resolves Accept-Language into IMultiLingualManager.CurrentLanguage and sets
// CultureInfo.CurrentUICulture to match. Identical to the EF Core sample: language resolution lives
// in the API package and knows nothing about the provider.
app.UseMultiLingual();

app.MapControllers();

// Language administration. In a real application: .RequireAuthorization("admin").
app.MapLanguageEndpoints();

// Convenience: report the language the API resolved for this request.
app.MapGet("/api/current-language", (IMultiLingualManager multiLingual) => Results.Ok(new
{
    resolved = multiLingual.CurrentLanguage.Code,
    direction = multiLingual.CurrentLanguage.Direction == DotNetBoost.MultiLingual.Core.Models.TextDirection.Rtl ? "rtl" : "ltr",
    defaultLanguage = multiLingual.DefaultLanguage.Code,
    baseLanguage = multiLingual.Languages.BaseLanguage,
    requestedLanguageHonoured = multiLingual.WasRequestedLanguageHonoured
}));

await DbInitializer.InitializeAsync(app.Services);

app.Run();

/// <summary>Exposed so an integration test project can drive the app with WebApplicationFactory.</summary>
public partial class Program;
