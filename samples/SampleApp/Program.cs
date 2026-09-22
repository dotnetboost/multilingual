using System.Text.Json.Serialization;
using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SampleApp.Data;
using SampleApp.Infrastructure;
using SampleApp.Infrastructure.OpenApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry, health checks, service discovery and resilient HttpClients. Everything this
// registers is inert when the app runs standalone; under the AppHost it is what makes the traces,
// metrics and logs show up on the Aspire dashboard.
builder.AddServiceDefaults();

// "multilingualdb" is the resource name from aspire/DotNetBoost.MultiLingual.AppHost/AppHost.cs;
// the AppHost injects ConnectionStrings__multilingualdb pointing at its container. Run the sample
// on its own and you supply it yourself, e.g. in the environment:
//   ConnectionStrings__multilingualdb="Host=localhost;Database=multilingual;Username=postgres;Password=…"
var connectionString = builder.Configuration.GetConnectionString("multilingualdb")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:multilingualdb is not set. Start the sample through the AppHost "
        + "(dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost), or set it to a PostgreSQL database.");

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
//
// AddJsonLocalization() serves IStringLocalizer<T> from Resources/en.json and Resources/ar.json, in
// the same language as the content and with the same fallback: French has no file, so French
// callers get English messages. The error responses in Infrastructure/AppException.cs use it.
builder.Services
    .AddMultiLingual(o => o.BaseLanguage = "en")
    .UseEntityFrameworkCore<AppDbContext>()
    .AddJsonLocalization()
    .Build();

// Running more than one instance? Each caches the language list, and only the instance that
// changes a language refreshes at once. Share a change signal through a distributed cache the
// instances already use, and every instance picks the change up within seconds:
//
//   builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);
//   builder.Services
//       .AddMultiLingual(o => o.BaseLanguage = "en")
//       .UseEntityFrameworkCore<AppDbContext>()
//       .SynchronizeLanguagesThroughDistributedCache()
//       .Build();
//
// Left out here: this sample is one process, so there is nobody to tell.

// ─────────────────────────────────────────────────────────────────────────────
//  Database — ACTIVE: PostgreSQL (Entity Framework Core)
//
//  Every alternative below is written out in full and commented — to switch, comment this block,
//  uncomment the one you want, and flip the matching ItemGroup in SampleApp.csproj and the
//  database block in AppHost.cs.
//
//  The (sp, options) overload, so the interceptors are resolved per request and see its language.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>((sp, options) => options
    .UseNpgsql(
        connectionString,
        // A cold Postgres container refuses a few connections before it accepts any, so retry
        // transient failures instead of failing the first request after a restart. Bounded
        // deliberately: the readiness probe below runs through this same strategy, and the default
        // policy (6 retries backing off to 30s) makes it take about a minute to report a database
        // that is plainly gone. The save interceptor runs each save inside the strategy, so a retry
        // replays the entity rows and their translations together.
        npgsql => npgsql.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(2),
            errorCodesToAdd: null))
    .UseMultiLingual(sp));

// Written out because the plain provider is used rather than Aspire's EF Core client integration:
//   readiness — without it /health answers Healthy while Postgres is unreachable, and the
//               AppHost's WithHttpHealthCheck("/health") would let the dashboard start anyway.
//   telemetry — Npgsql's ActivitySource and Meter, so a trace on the Aspire dashboard shows the SQL
//               a request ran — the Translations joins included — instead of stopping at the HTTP
//               span, and the Metrics page carries the connection-pool counters.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("Npgsql"))
    .WithMetrics(metrics => metrics.AddMeter("Npgsql"));

// ─────────────────────────────────────────────────────────────────────────────
//  Database — ALTERNATIVE: SQL Server (Entity Framework Core)
//  SampleApp.csproj: Microsoft.EntityFrameworkCore.SqlServer
// ─────────────────────────────────────────────────────────────────────────────
// builder.Services.AddDbContext<AppDbContext>((sp, options) => options
//     .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure())
//     .UseMultiLingual(sp));

// ─────────────────────────────────────────────────────────────────────────────
//  Database — ALTERNATIVE: SQLite (Entity Framework Core, no container)
//  SampleApp.csproj: Microsoft.EntityFrameworkCore.Sqlite
//  The zero-infrastructure option: `dotnet run` here with no AppHost at all. Replace the
//  connection string above with "Data Source=multilingual.db" and drop the Npgsql health check
//  and telemetry lines.
// ─────────────────────────────────────────────────────────────────────────────
// builder.Services.AddDbContext<AppDbContext>((sp, options) => options
//     .UseSqlite(connectionString)
//     .UseMultiLingual(sp));

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

// /health and /alive, polled by the AppHost to decide when the dashboard may start.
app.MapDefaultEndpoints();

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

// Resolves Accept-Language into the scoped ILanguageContext that the interceptors and the
// localizer read, and sets CultureInfo.CurrentUICulture to match.
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
