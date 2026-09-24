// ─────────────────────────────────────────────────────────────────────────────
//  DotNetBoost.MultiLingual — Aspire AppHost
//
//  Starts the whole stack with one `dotnet run --project` here — no Aspire CLI required,
//  though `aspire run` does the same if you have it installed:
//
//    • the database container holding the sample's tables and Translations  (PostgreSQL)
//    • the API      — samples/SampleApp
//    • the dashboard — clients/dashboard (Nuxt)
//
//  Exactly ONE database block below is active. Each alternative is written out in full and
//  commented; to switch, comment the active block, uncomment the one you want, uncomment its
//  PackageReference in DotNetBoost.MultiLingual.AppHost.csproj, and flip the matching blocks
//  in samples/SampleApp/Program.cs and samples/SampleApp/SampleApp.csproj.
// ─────────────────────────────────────────────────────────────────────────────

var builder = DistributedApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
//  Database — ACTIVE: PostgreSQL (Entity Framework Core)
//  Package: Aspire.Hosting.PostgreSQL
// ─────────────────────────────────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres")
    // Named volume, so languages and translations written in one session survive the next run.
    .WithDataVolume("dotnetboost-multilingual-pgdata")
    // Keeps the container alive between AppHost runs — a cold start costs a few seconds.
    .WithLifetime(ContainerLifetime.Persistent)
    // Browser-based SQL client at its own dashboard link: Products.Name holds English and
    // Translations everything else, which is the quickest way to see base-language mode.
    .WithPgWeb();

var multilingualDb = postgres.AddDatabase("multilingualdb");

// ─────────────────────────────────────────────────────────────────────────────
//  Database — ALTERNATIVE: SQL Server
//  Package: Aspire.Hosting.SqlServer
// ─────────────────────────────────────────────────────────────────────────────
// var sqlServer = builder.AddSqlServer("sqlserver")
//     .WithDataVolume("dotnetboost-multilingual-mssqldata")
//     .WithLifetime(ContainerLifetime.Persistent);
//
// var multilingualDb = sqlServer.AddDatabase("multilingualdb");

// ─────────────────────────────────────────────────────────────────────────────
//  Database — ALTERNATIVE: SQLite
//  No container and no hosting package: SQLite is a file next to the API. Delete the
//  WithReference(multilingualDb)/WaitFor(multilingualDb) lines on the api resource below and
//  let samples/SampleApp/Program.cs point at its own "Data Source=multilingual.db".
// ─────────────────────────────────────────────────────────────────────────────

// ─────────────────────────────────────────────────────────────────────────────
//  Database — ALTERNATIVE: MongoDB
//  Package: Aspire.Hosting.MongoDB
//
//  The resource name stays "multilingualdb", so the API reads the same
//  ConnectionStrings:multilingualdb and nothing below this block changes. Mongo Express is the
//  counterpart to WithPgWeb above: the quickest way to see that a product's document holds the
//  English name while the translations collection holds Arabic and French, one document per
//  property per language.
//
//  WithReplicaSet: a single node is enough, and it is what makes transactions available — the
//  provider's store.WithSession(session) needs one, and against a standalone mongod a document and
//  its translations cannot commit together. See the MongoDB section of README.md.
// ─────────────────────────────────────────────────────────────────────────────
// var mongo = builder.AddMongoDB("mongo")
//     .WithDataVolume("dotnetboost-multilingual-mongodata")
//     .WithLifetime(ContainerLifetime.Persistent)
//     .WithReplicaSet()
//     .WithMongoExpress();
//
// var multilingualDb = mongo.AddDatabase("multilingualdb");

// ── API — samples/SampleApp ──────────────────────────────────────────────────
// Its endpoints come from samples/SampleApp/Properties/launchSettings.json. Without that
// file the resource has no endpoint at all, and api.GetEndpoint("http") below — the URL
// the dashboard is handed — has nothing to resolve to.
var api = builder.AddProject<Projects.SampleApp>("api")
    .WithReference(multilingualDb)
    .WaitFor(multilingualDb)
    // /health carries the AddDbContextCheck registered in samples/SampleApp/Program.cs, so
    // this is what makes the dashboard's WaitFor(api) below hold until Postgres actually
    // answers rather than merely until the process is up. MapDefaultEndpoints only maps
    // /health in Development — drop this line if you ever publish this AppHost.
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// ── Dashboard — clients/dashboard (Nuxt) ─────────────────────────────────────
// npm install runs automatically before the dev server starts.
builder.AddJavaScriptApp("dashboard", "../../clients/dashboard", "dev")
    .WithNpm()
    // Nuxt binds whatever PORT says; without this it picks its own and Aspire has no
    // endpoint to publish, so the dashboard never gets a clickable URL.
    .WithHttpEndpoint(env: "PORT")
    .WithReference(api)
    .WaitFor(api)
    // Consumed by server/api/[...path].ts, which proxies the browser's calls.
    .WithEnvironment("NUXT_MULTILINGUAL_API_URL", api.GetEndpoint("http"))
    // The sidebar's "API reference" link. Read in the browser, so it needs the public URL.
    .WithEnvironment("NUXT_PUBLIC_API_REFERENCE_URL",
        ReferenceExpression.Create($"{api.GetEndpoint("http")}/scalar"))
    // Must match MultiLingualOptions.BaseLanguage in samples/SampleApp/Program.cs.
    .WithEnvironment("NUXT_PUBLIC_BASE_LANGUAGE", "en")
    .WithExternalHttpEndpoints();

builder.Build().Run();
