using System.Data;
using System.Text.Json.Serialization;
using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core.Interfaces;
using Npgsql;
using SampleApp.Dapper.Data;
using SampleApp.Dapper.Infrastructure.OpenApi;
using SampleApp.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry, health checks, service discovery and resilient HttpClients — as in the EF Core
// sample; inert standalone, and what feeds the Aspire dashboard under the AppHost.
builder.AddServiceDefaults();

// "multilingualdapperdb" is the resource name from aspire/DotNetBoost.MultiLingual.AppHost/AppHost.cs
// — a second database on the same PostgreSQL container the EF Core sample uses. Run this sample on
// its own and you supply it yourself:
//   ConnectionStrings__multilingualdapperdb="Host=localhost;Database=multilingualdapper;Username=postgres;Password=..."
var connectionString = builder.Configuration.GetConnectionString("multilingualdapperdb")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:multilingualdapperdb is not set. Start the sample through the AppHost "
        + "(dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost), or point it at a PostgreSQL "
        + "database — see samples/SampleApp.Dapper/README.md.");

// ── MultiLingual ─────────────────────────────────────────────────────────────
// The EF Core sample's chain with ONE line changed: UseDapper(...) instead of
// UseEntityFrameworkCore<AppDbContext>(). Everything else — the base language, the JSON localizer,
// Accept-Language resolution, the language endpoints — is provider-neutral and identical.
//
// The factory's runtime type is what selects the dialect: NpgsqlConnection here, and
// SqlConnection or SqliteConnection would need no other change. UseDapper registers it as a scoped
// IDbConnection, so the container disposes it and everything below — the controllers, the seeder,
// UnitOfWork — shares the very connection the library's stores use. That shared connection is what
// makes one transaction possible.
//
// migrateSchema: true creates the library's Languages and Translations tables at startup if they
// are absent, in the shape the EF Core provider maps. It is the right setting for a sample and for
// an application with no migrations of its own; leave it off when migrations or a deployment step
// own your schema. It only ever CREATEs — it will not migrate a table whose shape has changed.
// The sample's OWN tables are not its business: Data/DbInitializer.cs creates those.
//
// BaseLanguage = "en" keeps English in each table's OWN columns and every other language in
// Translations. A missing translation falls back to that column, so content is never null once it
// exists. Three consequences, all visible through the API: a product can only be CREATED with
// Accept-Language: en, English cannot be deleted or deactivated, and SELECT * FROM "Products"
// reads as English.
//
// Drop the argument and translatable properties have no column at all: every language, English
// included, becomes a row in Translations. With Dapper that means dropping them from this sample's
// DDL, INSERTs and SELECTs — there is no mapping to change, because the SQL is the mapping.
builder.Services
    .AddMultiLingual(o => o.BaseLanguage = "en")
    .UseDapper(_ => new NpgsqlConnection(connectionString), migrateSchema: true)
    .AddJsonLocalization()
    .Build();

// Seeds the sample's own tables and the three languages. A hosted service, registered AFTER the
// chain above on purpose: migrateSchema creates the library's tables from a hosted service too, and
// hosted services start in registration order — so this one finds them already there. See
// Data/DatabaseSeeder.cs.
builder.Services.AddHostedService<DatabaseSeeder>();

// ── One transaction per request ──────────────────────────────────────────────
// Registering UnitOfWork's transaction as the scope's IDbTransaction is all it takes: UseDapper
// resolves one when it builds its stores, so an entity's own INSERT and its translations' upserts
// land in the same transaction with nothing in the controllers saying so. See Data/UnitOfWork.cs —
// this is the seam the MongoDB sample cannot demonstrate, because the AppHost runs a standalone
// mongod and transactions there need a replica set.
builder.Services.AddScoped<UnitOfWork>();
builder.Services.AddScoped<IDbTransaction>(sp => sp.GetRequiredService<UnitOfWork>().Current);

// Readiness, as AddDbContextCheck<AppDbContext>() is for the EF Core sample.
builder.Services.AddHealthChecks()
    .AddCheck("postgresql", new NpgsqlHealthCheck(() => new NpgsqlConnection(connectionString)));

// Npgsql emits its own ActivitySource, so queries show up as spans on the Aspire dashboard with no
// extra package — the EF Core sample gets the same ones.

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
        .WithTitle("DotNetBoost.MultiLingual sample (Dapper)")
        .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient));

    app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();
}

// Commits the request's transaction when the response is a success. Registered here so it covers
// the language endpoints below as well as the controllers — see Data/UnitOfWorkMiddleware.cs.
app.UseMiddleware<UnitOfWorkMiddleware>();

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

// The seeding happens in DatabaseSeeder, not here: it has to run after migrateSchema's own hosted
// service, and only the host can order those.
app.Run();

/// <summary>Exposed so an integration test project can drive the app with WebApplicationFactory.</summary>
public partial class Program;
