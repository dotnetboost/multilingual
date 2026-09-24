using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using MongoDB.Bson;
using MongoDB.Driver;
using SampleApp.MongoDb;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// The AppHost injects this; running standalone, set it yourself — see README.md in this folder.
var connectionString = builder.Configuration.GetConnectionString("dishesdb")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:dishesdb is not set. Start this sample through the AppHost "
        + "(dotnet run --project aspire/DotNetBoost.MultiLingual.AppHost), or point it at a MongoDB "
        + "database — see samples/SampleApp.MongoDb/README.md.");

// ── The driver, registered as it expects: one client for the process. ─────────
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
builder.Services.AddSingleton(sp =>
{
    var url = MongoUrl.Create(connectionString);
    return sp.GetRequiredService<IMongoClient>().GetDatabase(url.DatabaseName ?? "dishes");
});
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoDatabase>().GetCollection<Dish>("dishes"));

// ── MultiLingual ─────────────────────────────────────────────────────────────
// The same chain as the EF Core sample with one line changed: UseMongoDb() instead of
// UseEntityFrameworkCore<AppDbContext>().
//
// BaseLanguage = "en" keeps English in each document's OWN fields and every other language in the
// translations collection. That is what makes `db.dishes.find()` readable — and it is also the one
// setting that puts a duty on this code: see PUT below.
//
// Drop the argument and translatable properties are unmapped from the document entirely; every
// language, English included, becomes a translation document. Nothing else in this file changes.
builder.Services
    .AddMultiLingual(o => o.BaseLanguage = "en")
    .UseMongoDb()
    .Build();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<MultiLingualExceptionHandler>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseExceptionHandler();

// Resolves Accept-Language into IMultiLingualManager.CurrentLanguage. Identical to the EF Core
// sample: language resolution is in the API package and knows nothing about the provider.
app.UseMultiLingual();

// Language administration, also provider-neutral.
app.MapLanguageEndpoints();

var dishes = app.Services.GetRequiredService<IMongoCollection<Dish>>();

// ─────────────────────────────────────────────────────────────────────────────
//  READ — localize after the query
//
//  This is the first of the two things an EF Core application never writes. A driver query fills a
//  document's own fields; the translatable ones arrive null until LocalizeAsync fills them, in the
//  request's language, falling back to the default.
// ─────────────────────────────────────────────────────────────────────────────
app.MapGet("/api/dishes", async (
    IMultiLingualManager multiLingual,
    string? search,
    CancellationToken ct) =>
{
    var all = await dishes.Find(Builders<Dish>.Filter.Empty).SortBy(d => d.Price).ToListAsync(ct);

    await multiLingual.LocalizeAsync(all, ct);

    // The one thing the EF Core provider does that no driver query can: filter and sort on
    // TRANSLATED text inside the database. It works there by rewriting the LINQ query, so it has no
    // MongoDB equivalent — the filter happens here instead, after the values are resolved. Fine for
    // a page of results; a large collection wants a $lookup pipeline against the translations
    // collection, or a denormalised per-language field.
    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim();
        all = all
            .Where(d => d.Name is not null && d.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    return Results.Ok(all.Select(d => ToResponse(d, multiLingual)).ToList());
});

app.MapGet("/api/dishes/{id}", async (
    string id,
    IMultiLingualManager multiLingual,
    CancellationToken ct) =>
{
    var dish = await FindAsync(id, ct);
    if (dish is null)
        return Results.NotFound();

    await multiLingual.LocalizeAsync([dish], ct);

    return Results.Ok(ToResponse(dish, multiLingual));
});

// Every stored language for one dish — no localizing needed, and the same call the EF Core sample
// makes. The quickest way to see what a write actually did.
app.MapGet("/api/dishes/{id}/translations", async (
    string id,
    IMultiLingualManager multiLingual,
    CancellationToken ct) =>
{
    var dish = await FindAsync(id, ct);
    if (dish is null)
        return Results.NotFound();

    var stored = await multiLingual.For(dish).GetAllAsync(ct);

    return Results.Ok(new DishTranslationsResponse(dish.Id.ToString(), stored));
});

// ─────────────────────────────────────────────────────────────────────────────
//  CREATE — the document first, then its translations
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/dishes", async (
    CreateDishRequest request,
    IMultiLingualManager multiLingual,
    CancellationToken ct) =>
{
    // In base-language mode an entity may only be created in the base language, because every
    // translation hangs off the base value. The EF Core save interceptor enforces that; nothing
    // intercepts a driver insert, so this check is the controller's — without it, an Arabic request
    // would put Arabic text in the field that is supposed to hold English.
    //
    // The same exception EF Core throws, so MultiLingualExceptionHandler still answers 409.
    if (!multiLingual.Languages.IsBaseLanguage(multiLingual.CurrentLanguage.Code))
    {
        throw new BaseLanguageRequiredException(
            nameof(Dish), multiLingual.Languages.BaseLanguage!, multiLingual.CurrentLanguage.Code);
    }

    var dish = new Dish
    {
        Price = request.Price,
        Name = request.Name,
        Description = request.Description
    };

    // The document first: the driver assigns Id here, and translations need a key to hang off.
    // Calling SaveAsync before this would be refused — ObjectId.Empty is not a key.
    await dishes.InsertOneAsync(dish, cancellationToken: ct);

    // In base-language mode the English text is already in the document, so this writes nothing for
    // English; it is what writes anything Set(...) queued for another language. Without a base
    // language it is what stores the English too.
    await multiLingual.For(dish).SaveAsync(ct);

    return Results.Created($"/api/dishes/{dish.Id}", ToResponse(dish, multiLingual));
});

// ─────────────────────────────────────────────────────────────────────────────
//  UPDATE — the part that is easy to get wrong
//
//  A null field means "not supplied", exactly as in the EF Core sample. Two things the EF save
//  interceptor infers from IsModified have to be said out loud here, and both only matter in
//  base-language mode:
//
//    • the translatable fields belong in the DOCUMENT only when the request's language is the base
//      one — that field is where the base language lives. In any other language they belong in
//      translation documents, so the $set below leaves them out and SaveAsync files them instead.
//    • a translatable property the caller did NOT supply still holds whatever the read served.
//      MarkAsBaseLanguageValues says so, and without it the next save files that English as a
//      translation in the request's language — quietly turning English into Arabic.
// ─────────────────────────────────────────────────────────────────────────────
app.MapPut("/api/dishes/{id}", async (
    string id,
    UpdateDishRequest request,
    IMultiLingualManager multiLingual,
    CancellationToken ct) =>
{
    var dish = await FindAsync(id, ct);
    if (dish is null)
        return Results.NotFound();

    // Before anything is assigned, so the engine can tell a value the read served from one the
    // caller authored.
    await multiLingual.LocalizeAsync([dish], ct);

    var updates = new List<UpdateDefinition<Dish>>();

    if (request.Price is not null)
        updates.Add(Builders<Dish>.Update.Set(d => d.Price, request.Price.Value));

    if (request.Name is not null)
        dish.Name = request.Name;
    else
        multiLingual.For(dish).MarkAsBaseLanguageValues(d => d.Name);

    if (request.Description is not null)
        dish.Description = request.Description;
    else
        multiLingual.For(dish).MarkAsBaseLanguageValues(d => d.Description);

    if (multiLingual.Languages.IsBaseLanguage(multiLingual.CurrentLanguage.Code))
    {
        if (request.Name is not null)
            updates.Add(Builders<Dish>.Update.Set(d => d.Name, request.Name));

        if (request.Description is not null)
            updates.Add(Builders<Dish>.Update.Set(d => d.Description, request.Description));
    }

    if (updates.Count > 0)
    {
        await dishes.UpdateOneAsync(
            d => d.Id == dish.Id, Builders<Dish>.Update.Combine(updates), cancellationToken: ct);
    }

    await multiLingual.For(dish).SaveAsync(ct);

    // Re-read, so the response shows what is stored for the requested language.
    var saved = await dishes.Find(d => d.Id == dish.Id).FirstAsync(ct);
    await multiLingual.LocalizeAsync([saved], ct);

    return Results.Ok(ToResponse(saved, multiLingual));
});

// ─────────────────────────────────────────────────────────────────────────────
//  DELETE
// ─────────────────────────────────────────────────────────────────────────────

// Removes one language's translation of a dish — every translatable property in it. Reads in that
// language then fall back. English is refused with a 409: it is the document's own field, not a
// translation. Clear() only queues the removal, so SaveAsync applies it; with EF Core, SaveChanges
// would.
app.MapDelete("/api/dishes/{id}/translations/{languageCode}", async (
    string id,
    string languageCode,
    IMultiLingualManager multiLingual,
    CancellationToken ct) =>
{
    var dish = await FindAsync(id, ct);
    if (dish is null)
        return Results.NotFound();

    multiLingual.For(dish).Clear(languageCode);
    await multiLingual.For(dish).SaveAsync(ct);

    return Results.Ok(new { id = dish.Id.ToString(), cleared = LanguageCodes.Normalize(languageCode) });
});

// The save interceptor removes a deleted entity's translations for you. Here it is one more call.
//
// Outside a transaction these are two writes, so a crash between them leaves translations whose
// document is gone — harmless, since nothing reads them, but a session closes the gap:
//
//   using var session = await client.StartSessionAsync(ct);
//   session.StartTransaction();
//   await dishes.DeleteOneAsync(session, d => d.Id == dish.Id, cancellationToken: ct);
//   await writer.WithStore(store.WithSession(session)).DeleteEntitiesAsync(…, ct);
//   await session.CommitTransactionAsync(ct);
//
// A transaction needs a replica set, which is what the AppHost starts. See docs/mongodb.md.
app.MapDelete("/api/dishes/{id}", async (
    string id,
    IMultiLingualManager multiLingual,
    CancellationToken ct) =>
{
    var dish = await FindAsync(id, ct);
    if (dish is null)
        return Results.NotFound();

    await dishes.DeleteOneAsync(d => d.Id == dish.Id, ct);
    await multiLingual.For(dish).DeleteAsync(ct);

    return Results.Ok(new { id = dish.Id.ToString(), deleted = true });
});

// Which language the API resolved for this request.
app.MapGet("/api/current-language", (IMultiLingualManager multiLingual) => Results.Ok(new
{
    resolved = multiLingual.CurrentLanguage.Code,
    direction = multiLingual.CurrentLanguage.Direction == TextDirection.Rtl ? "rtl" : "ltr",
    defaultLanguage = multiLingual.DefaultLanguage.Code,
    baseLanguage = multiLingual.Languages.BaseLanguage,
    requestedLanguageHonoured = multiLingual.WasRequestedLanguageHonoured
}));

await SeedAsync(app.Services);

app.Run();

// ─────────────────────────────────────────────────────────────────────────────

async Task<Dish?> FindAsync(string id, CancellationToken ct) =>
    ObjectId.TryParse(id, out var objectId)
        ? await dishes.Find(d => d.Id == objectId).FirstOrDefaultAsync(ct)
        : null;

static DishResponse ToResponse(Dish dish, IMultiLingualManager multiLingual) =>
    new(dish.Id.ToString(), dish.Price, dish.Name, dish.Description, multiLingual.CurrentLanguage.Code);

// Three languages and three dishes, with translations deliberately missing so the fallback shows on
// the first request. The language and Set(...) calls are identical to the EF Core sample's seed —
// only the writes around them differ: one SaveAsync per document instead of one SaveChanges.
//
// No language is resolved here, so SaveAsync writes only what Set(...) queued, and the
// "create only in the base language" rule has no Accept-Language to be wrong about.
async Task SeedAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var multiLingual = scope.ServiceProvider.GetRequiredService<IMultiLingualManager>();

    if ((await multiLingual.Languages.GetAllAsync()).Count > 0)
        return;

    await multiLingual.Languages.CreateAsync(new NewLanguage { Code = "en", Name = "English" });
    await multiLingual.Languages.CreateAsync(new NewLanguage { Code = "ar", Name = "العربية" });
    await multiLingual.Languages.CreateAsync(new NewLanguage { Code = "fr", Name = "Français" });

    var hummus = new Dish { Price = 4.50m, Name = "Hummus", Description = "Chickpeas, tahini, lemon." };
    multiLingual.For(hummus).Set("ar", d => d.Name, "حمص").Set("ar", d => d.Description, "حمص وطحينة وليمون.");
    multiLingual.For(hummus).Set("fr", d => d.Name, "Houmous");

    var falafel = new Dish { Price = 3.20m, Name = "Falafel", Description = "Fried chickpea patties." };
    multiLingual.For(falafel).Set("ar", d => d.Name, "فلافل");
    // No French: GET with Accept-Language: fr falls back to the document's English field.

    var tabbouleh = new Dish { Price = 5.00m, Name = "Tabbouleh", Description = "Parsley, bulgur, mint." };
    // English only, and no translation documents at all.

    foreach (var dish in new[] { hummus, falafel, tabbouleh })
    {
        await dishes.InsertOneAsync(dish);
        await multiLingual.For(dish).SaveAsync();
    }
}
