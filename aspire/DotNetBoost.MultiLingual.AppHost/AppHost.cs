// ─────────────────────────────────────────────────────────────────────────────
//  DotNetBoost.MultiLingual — Aspire AppHost
//
//  Starts the whole stack with one `dotnet run --project` here — no Aspire CLI required,
//  though `aspire run` does the same if you have it installed:
//
//    • the database container holding the sample's tables and Translations  (PostgreSQL)
//    • the API       — samples/SampleApp            (EF Core provider)
//    • the same API  — samples/SampleApp.Dapper     (Dapper provider, second database on the same server)
//    • MongoDB
//    • the same API  — samples/SampleApp.MongoDb    (MongoDB provider)
//    • the dashboard — clients/dashboard (Nuxt), pointed at any one of them
//
//  All three providers run at once, on purpose: the same library, the same endpoints, three very
//  different ways of reaching a database.
//
//  The EF Core sample's ENGINE is swappable — exactly one of its database blocks below is active,
//  each alternative written out and commented. To switch, comment the active block, uncomment the
//  one you want, uncomment its PackageReference in DotNetBoost.MultiLingual.AppHost.csproj, and
//  flip the matching blocks in samples/SampleApp/Program.cs and samples/SampleApp/SampleApp.csproj.
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

// A second database on the SAME server, for samples/SampleApp.Dapper. Its own database rather than
// its own container: the two samples' tables have the same names, and seeing Dapper create its
// Languages and Translations from scratch — which is what migrateSchema: true does — is the point.
// pgweb above reaches both, so the two schemas can be compared side by side; they are identical,
// which is exactly the claim the Dapper provider makes.
var multilingualDapperDb = postgres.AddDatabase("multilingualdapperdb");

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
//  MongoDB — for samples/SampleApp.MongoDb
//  Package: Aspire.Hosting.MongoDB
//
//  Not an alternative to the block above: a second provider running beside it, so both are up at
//  once and the difference is something you can click through rather than read about.
//
//  Mongo Express is the counterpart to WithPgWeb: the quickest way to see that a product's document
//  holds the English name while the translations collection holds Arabic and French, one document
//  per property per language.
//
//  This is a STANDALONE mongod — Aspire.Hosting.MongoDB 13.5.2 has no WithReplicaSet — so
//  transactions are not available here, and the provider's store.WithSession(session) has nothing
//  to enlist in. The sample does not need them. To try them, run a one-node replica set yourself
//  and point the sample at it; docs/mongodb.md has the command.
// ─────────────────────────────────────────────────────────────────────────────
var mongo = builder.AddMongoDB("mongo")
    .WithDataVolume("dotnetboost-multilingual-mongodata")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithMongoExpress();

var mongoDb = mongo.AddDatabase("multilingualmongodb");

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

// ── API — samples/SampleApp.MongoDb ──────────────────────────────────────────
// The same API as above over the MongoDB provider: the same routes, the same request and response
// bodies (literally the same DTO files), and the same Products and Categories. What differs is
// inside the controllers — see docs/mongodb.md.
var mongoApi = builder.AddProject<Projects.SampleApp_MongoDb>("mongo-api")
    .WithReference(mongoDb)
    .WaitFor(mongoDb)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// ── API — samples/SampleApp.Dapper ───────────────────────────────────────────
// The same API again over the Dapper provider: the same routes, the same request and response
// bodies (literally the same DTO files), and the same Products and Categories.
//
// Two things about this one are worth clicking through. Its Languages and Translations tables are
// created at startup by UseDapper(..., migrateSchema: true) — nothing else creates them, so an
// empty database is enough — and they come out identical to the ones EF Core migrates into
// multilingualdb next door. And every request runs in a real transaction, so a row and its
// translations commit or roll back together, which the MongoDB resource above cannot show: that is
// a standalone mongod and transactions there need a replica set.
var dapperApi = builder.AddProject<Projects.SampleApp_Dapper>("dapper-api")
    .WithReference(multilingualDapperDb)
    .WaitFor(multilingualDapperDb)
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
    //
    // Swap `api` for `mongoApi` or `dapperApi` here and the dashboard drives that API instead, with
    // no change to the dashboard itself: all three serve the same routes and the same bodies. That
    // is worth doing once — it is the clearest evidence that the provider is invisible from outside.
    .WithEnvironment("NUXT_MULTILINGUAL_API_URL", api.GetEndpoint("http"))
    // The sidebar's "API reference" link. Read in the browser, so it needs the public URL.
    .WithEnvironment("NUXT_PUBLIC_API_REFERENCE_URL",
        ReferenceExpression.Create($"{api.GetEndpoint("http")}/scalar"))
    // Must match MultiLingualOptions.BaseLanguage in samples/SampleApp/Program.cs.
    .WithEnvironment("NUXT_PUBLIC_BASE_LANGUAGE", "en")
    .WithExternalHttpEndpoints();

builder.Build().Run();
