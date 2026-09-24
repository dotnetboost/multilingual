using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SampleApp.Data;
using SampleApp.Domain;
using SampleApp.Dtos;
using SampleApp.Infrastructure;

namespace SampleApp.Controllers;

/// <summary>
/// The functional proof of the mechanism. Notice what is NOT here: no translation table
/// access, no per-language joins, no mapping of translated fields. The controller reads and
/// writes <c>Product.Name</c> like any other property; the interceptors do the rest.
/// </summary>
[ApiController]
[Route("api/products")]
[Produces("application/json")]
public sealed class ProductsController(
    AppDbContext db,
    IMultiLingualManager multiLingual,
    IStringLocalizer<SharedResource> localizer)
    : ControllerBase
{
    // ─────────────────────────────────────────────────────────────────────────
    //  The same controller on MongoDB
    //
    //  Every action below is followed by a commented MongoDB version. The library calls are the
    //  same ones — LocalizeAsync, For(entity).Set / Clear / SaveAsync / DeleteAsync — because the
    //  engine is provider-neutral. What changes is that there is no change tracker to hook, so
    //  three jobs the EF Core interceptors do quietly become the controller's:
    //
    //    1. LOCALIZE after a query. A driver query fills a document's own fields; the translatable
    //       ones are filled by LocalizeAsync(entities).
    //    2. SAVE translations after the document, so its key already exists. For(entity).SaveAsync()
    //       writes what Set(...) queued plus what the properties hold for the request's language.
    //    3. In base-language mode, keep non-base text out of the base field — see Update — and
    //       enforce "create only in the base language" yourself, which Create does.
    //
    //  Filtering and sorting on TRANSLATED text inside the database stays EF Core only: it works by
    //  rewriting the LINQ query, and a driver query has no equivalent. GetAll below shows what to
    //  do instead.
    //
    //  To switch, flip the blocks in samples/SampleApp/Program.cs, SampleApp.csproj and
    //  aspire/DotNetBoost.MultiLingual.AppHost/AppHost.cs. In this file, the usings become
    //
    //    + using DotNetBoost.MultiLingual.Core.Metadata;   // MultiLingualMetadata, EntityKey
    //    + using MongoDB.Bson;                             // BsonDocument, in NextIdAsync
    //    + using MongoDB.Driver;
    //    - using DotNetBoost.MultiLingual.EntityFrameworkCore;
    //    - using Microsoft.EntityFrameworkCore;
    //    - using SampleApp.Data;
    //
    //  and the constructor becomes:
    //
    //    public sealed class ProductsController(
    //        IMongoDatabase database,
    //        IMultiLingualManager multiLingual,
    //        ITranslationWriter writer,                  // bulk translation deletes only
    //        IStringLocalizer<SharedResource> localizer) : ControllerBase
    //    {
    //        private IMongoCollection<Product> Products => database.GetCollection<Product>("products");
    //        private IMongoCollection<Category> Categories => database.GetCollection<Category>("categories");
    //
    //  The entities keep their `int Id`, so every route and DTO above still lines up. A greenfield
    //  MongoDB model would use ObjectId, which the library reads just as happily — and treats
    //  ObjectId.Empty as "not saved yet". The server does not generate integers, so Create
    //  allocates them from a counters collection (NextIdAsync, at the bottom of this file).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists products, each resolved to the requested language
    /// (<c>Accept-Language</c>, then the system default).
    /// <para>
    /// <c>search</c> and <c>sort</c> work on the TRANSLATED name — the one this response shows —
    /// in the database. This is plain EF Core: every query on the context is translated, so
    /// <c>Where(p =&gt; p.Name.Contains(…))</c> and <c>OrderBy(p =&gt; p.Name)</c> use the requested
    /// language, falling back exactly as the read does. The category filter stays an ordinary
    /// indexed column filter.
    /// </para>
    /// </summary>
    /// <param name="categoryId">Only products in this category.</param>
    /// <param name="search">Part of the name, in the requested language (case-insensitive: PostgreSQL <c>ILIKE</c>).</param>
    /// <param name="sort"><c>name</c> or <c>-name</c> for the translated name; the id otherwise.</param>
    /// <param name="ct">Cancellation.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ProductResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ProductResponse>>> GetAll(
        [FromQuery] int? categoryId,
        [FromQuery] string? search,
        [FromQuery] string? sort,
        CancellationToken ct)
    {
        var query = db.Products.AsNoTracking();

        if (categoryId is not null)
        {
            query = query.Where(p => p.CategoryId == categoryId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            // ILIKE: PostgreSQL's LIKE is case-sensitive. On SQL Server or SQLite, EF.Functions.Like.
            var pattern = $"%{search.Trim()}%";
            query = query.Where(p => p.Name != null && EF.Functions.ILike(p.Name, pattern));
        }

        query = sort?.Trim().ToLowerInvariant() switch
        {
            null or "" or "id" => query.OrderBy(p => p.Id),
            "name"             => query.OrderBy(p => p.Name).ThenBy(p => p.Id),
            "-name"            => query.OrderByDescending(p => p.Name).ThenBy(p => p.Id),
            _                  => throw new ValidationException("Products.UnknownSort", sort)
        };

        // One statement: the products, their translated names for the filter and the sort, and
        // their translated values — Name and Description are filled in before this returns.
        var products = await query.ToListAsync(ct);

        return Ok(products.Select(ToResponse).ToList());
    }

    //  MongoDB — GetAll. The category filter is an ordinary field filter and stays in the
    //  database; `search` and `sort` are on the translated name, which no driver query can reach,
    //  so they move after LocalizeAsync. Narrow in the database as much as the request allows
    //  first — a collection large enough for that to matter wants a $lookup pipeline against the
    //  translations collection, or a denormalised per-language field, instead of this.
    //
    //  var filter = Builders<Product>.Filter.Empty;
    //  if (categoryId is not null)
    //      filter &= Builders<Product>.Filter.Eq(p => p.CategoryId, categoryId);
    //
    //  var products = await Products.Find(filter).SortBy(p => p.Id).ToListAsync(ct);
    //
    //  // Name and Description are null until this runs.
    //  await multiLingual.LocalizeAsync(products, ct);
    //
    //  if (!string.IsNullOrWhiteSpace(search))
    //  {
    //      var term = search.Trim();
    //      products = products
    //          .Where(p => p.Name is not null && p.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
    //          .ToList();
    //  }
    //
    //  products = sort?.Trim().ToLowerInvariant() switch
    //  {
    //      null or "" or "id" => products.OrderBy(p => p.Id).ToList(),
    //      "name"             => products.OrderBy(p => p.Name, StringComparer.CurrentCulture).ThenBy(p => p.Id).ToList(),
    //      "-name"            => products.OrderByDescending(p => p.Name, StringComparer.CurrentCulture).ThenBy(p => p.Id).ToList(),
    //      _                  => throw new ValidationException("Products.UnknownSort", sort)
    //  };
    //
    //  return Ok(products.Select(ToResponse).ToList());

    /// <summary>
    /// Lists products straight into a small DTO, with the category's name alongside — in the
    /// requested language, in one query.
    /// <para>
    /// This is the projection shape most read endpoints use, and a plain <c>Select</c> cannot do
    /// it here: <c>p.Name</c> and <c>p.Category.Name</c> are English columns in this sample's
    /// base-language mode — but every query is translated, so each translatable property is read
    /// in the request's language instead, navigations included. (<c>IgnoreTranslations()</c> would
    /// read the English columns.)
    /// </para>
    /// </summary>
    [HttpGet("summaries")]
    [ProducesResponseType<IReadOnlyList<ProductSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProductSummaryResponse>>> GetSummaries(CancellationToken ct)
    {
        var languageCode = multiLingual.CurrentLanguage.Code;

        var summaries = await db.Products
            .AsNoTracking()
            
            .OrderBy(p => p.Id)
            .Select(p => new ProductSummaryResponse(p.Id, p.Sku, p.Price, p.Name, p.Category!.Name, languageCode))
            .ToListAsync(ct);

        return Ok(summaries);
    }

    //  MongoDB — GetSummaries. EF Core reads the product and its category's translated name in one
    //  statement; here each collection is read and localized on its own, then joined in memory.
    //  LocalizeAsync batches its reads per entity type, so this is two translation queries in
    //  total however many products come back — not one per row.
    //
    //  var languageCode = multiLingual.CurrentLanguage.Code;
    //
    //  var products   = await Products.Find(Builders<Product>.Filter.Empty).SortBy(p => p.Id).ToListAsync(ct);
    //  var categories = await Categories.Find(Builders<Category>.Filter.Empty).ToListAsync(ct);
    //
    //  await multiLingual.LocalizeAsync(products, ct);
    //  await multiLingual.LocalizeAsync(categories, ct);
    //
    //  var categoryNames = categories.ToDictionary(c => c.Id, c => c.Name);
    //
    //  return Ok(products
    //      .Select(p => new ProductSummaryResponse(
    //          p.Id, p.Sku, p.Price, p.Name,
    //          p.CategoryId is { } id && categoryNames.TryGetValue(id, out var name) ? name : null,
    //          languageCode))
    //      .ToList());

    [HttpGet("{id:int}")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponse>> Get(int id, CancellationToken ct)
    {
        var product = await db.Products
            .AsNoTracking()
            .Where(p => p.Id == id)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Products.NotFound", id);

        return Ok(ToResponse(product));
    }

    //  MongoDB — Get.
    //
    //  var product = await Products.Find(p => p.Id == id).FirstOrDefaultAsync(ct)
    //      ?? throw new NotFoundException("Products.NotFound", id);
    //
    //  await multiLingual.LocalizeAsync([product], ct);
    //
    //  return Ok(ToResponse(product));

    /// <summary>Every stored language for one product — the easiest way to verify a write.</summary>
    [HttpGet("{id:int}/translations")]
    [ProducesResponseType<ProductTranslationsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductTranslationsResponse>> GetTranslations(int id, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("Products.NotFound", id);

        var stored = await multiLingual.For(product).GetAllAsync(ct);

        return Ok(new ProductTranslationsResponse(product.Id, product.Sku, stored));
    }

    //  MongoDB — GetTranslations. Only the load differs: reading every stored language for one
    //  entity is the same manager call, and needs no localizing first.
    //
    //  var product = await Products.Find(p => p.Id == id).FirstOrDefaultAsync(ct)
    //      ?? throw new NotFoundException("Products.NotFound", id);
    //
    //  var stored = await multiLingual.For(product).GetAllAsync(ct);
    //
    //  return Ok(new ProductTranslationsResponse(product.Id, product.Sku, stored));

    /// <summary>
    /// Creates a product. English is the base language, so <c>Name</c>/<c>Description</c> are
    /// stored as columns on <c>Products</c> and the request must carry
    /// <c>Accept-Language: en</c> — anything else is answered with a 409, because every
    /// translation hangs off the base value. Add other languages afterwards with <c>PUT</c>.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductResponse>> Create(
        [FromBody] CreateProductRequest request,
        CancellationToken ct)
    {
        await EnsureCategoryExistsAsync(request.CategoryId, ct);

        var sku = request.Sku.Trim();
        if (await db.Products.AnyAsync(p => p.Sku == sku, ct))
        {
            throw new ConflictException("Products.SkuExists", sku);
        }

        var product = new Product
        {
            Sku = sku,
            Price = request.Price,
            CategoryId = request.CategoryId,
            Name = request.Name,
            Description = request.Description
        };

        db.Products.Add(product);

        // Base language, so Name/Description go straight into the Products row. In a language
        // other than the base one, the interceptor refuses the create instead.
        await db.SaveChangesAsync(ct);

        // The localizer answers in the request's language, from Resources/{language}.json.
        var created = ToResponse(product) with { Message = localizer["Products.Created", product.Sku] };

        return CreatedAtAction(nameof(Get), new { id = product.Id }, created);
    }

    //  MongoDB — Create. Two things the EF save interceptor did are explicit here: the
    //  base-language rule, and writing the translations after the document.
    //
    //  // The interceptor refuses a create in any language but the base one, because every
    //  // translation hangs off the base value. Nothing intercepts a driver insert, so without this
    //  // check an Arabic request would put Arabic text in the field that is supposed to hold
    //  // English. BaseLanguageRequiredException is the same exception EF Core throws, so
    //  // MultiLingualExceptionHandler still answers 409.
    //  if (!multiLingual.Languages.IsBaseLanguage(multiLingual.CurrentLanguage.Code))
    //  {
    //      throw new BaseLanguageRequiredException(
    //          nameof(Product), multiLingual.Languages.BaseLanguage!, multiLingual.CurrentLanguage.Code);
    //  }
    //
    //  await EnsureCategoryExistsAsync(request.CategoryId, ct);
    //
    //  var sku = request.Sku.Trim();
    //  if (await Products.Find(p => p.Sku == sku).AnyAsync(ct))
    //  {
    //      throw new ConflictException("Products.SkuExists", sku);
    //  }
    //
    //  var product = new Product
    //  {
    //      Id = await NextIdAsync("products", ct),
    //      Sku = sku,
    //      Price = request.Price,
    //      CategoryId = request.CategoryId,
    //      Name = request.Name,
    //      Description = request.Description
    //  };
    //
    //  // The document first, so its key exists before anything refers to it. In base-language mode
    //  // Name and Description are still mapped fields — that field IS the base language — so the
    //  // insert stores the English text, and SaveAsync finds nothing to file as a translation.
    //  // Without a base language they are unmapped, the document carries neither, and SaveAsync is
    //  // what stores the English. Either way the call belongs here: it is also what writes anything
    //  // an earlier Set(...) queued.
    //  await Products.InsertOneAsync(product, cancellationToken: ct);
    //  await multiLingual.For(product).SaveAsync(ct);
    //
    //  var created = ToResponse(product) with { Message = localizer["Products.Created", product.Sku] };
    //
    //  return CreatedAtAction(nameof(Get), new { id = product.Id }, created);

    /// <summary>
    /// Updates a product. <c>Name</c>/<c>Description</c> are written for the request language
    /// (<c>Accept-Language</c>) only; other languages are untouched. With <c>en</c> that means the
    /// columns on <c>Products</c>; with any other language, a row in <c>Translations</c>.
    /// <para>
    /// Every field is optional and a <c>null</c> means "leave it alone", which is why only supplied
    /// values are assigned below. The loaded entity reads in the request's language; a value
    /// nobody assigns is recognised as read, not authored, and is never written back.
    /// </para>
    /// </summary>
    [HttpPut("{id:int}")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductResponse>> Update(
        int id,
        [FromBody] UpdateProductRequest request,
        CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("Products.NotFound", id);

        if (request.Sku is not null)
        {
            var sku = request.Sku.Trim();
            if (await db.Products.AnyAsync(p => p.Sku == sku && p.Id != id, ct))
            {
                throw new ConflictException("Products.SkuExists", sku);
            }

            product.Sku = sku;
        }

        if (request.Price is not null)
        {
            product.Price = request.Price.Value;
        }

        if (request.CategoryId is not null)
        {
            await EnsureCategoryExistsAsync(request.CategoryId, ct);
            product.CategoryId = request.CategoryId;
        }

        // Only what the request actually supplied. These are real columns in base-language mode,
        // so assigning null would erase the stored English rather than mean "not supplied" — and
        // a property nobody assigns keeps the value the query read, which the engine recognises
        // as read rather than authored: it is neither filed as a translation nor written to the
        // English column.
        if (request.Name is not null)
        {
            product.Name = request.Name;
        }

        if (request.Description is not null)
        {
            product.Description = request.Description;
        }

        await db.SaveChangesAsync(ct);

        // Re-read so the response shows what is actually stored for the requested language.
        db.ChangeTracker.Clear();
        var saved = await db.Products
            .AsNoTracking()
            .Where(p => p.Id == id)
            .FirstOrDefaultAsync(ct);

        return Ok(ToResponse(saved!) with { Message = localizer["Products.Updated", saved!.Sku] });
    }

    //  MongoDB — Update. The subtle one, and worth reading even if you never use MongoDB: it spells
    //  out what the EF interceptor infers from IsModified.
    //
    //  var product = await Products.Find(p => p.Id == id).FirstOrDefaultAsync(ct)
    //      ?? throw new NotFoundException("Products.NotFound", id);
    //
    //  // Localize before assigning anything, so the properties hold what a READ served — which is
    //  // what lets the engine tell a value nobody touched from one the caller authored.
    //  await multiLingual.LocalizeAsync([product], ct);
    //
    //  var updates = new List<UpdateDefinition<Product>>();
    //
    //  if (request.Sku is not null)
    //  {
    //      var sku = request.Sku.Trim();
    //      if (await Products.Find(p => p.Sku == sku && p.Id != id).AnyAsync(ct))
    //      {
    //          throw new ConflictException("Products.SkuExists", sku);
    //      }
    //
    //      updates.Add(Builders<Product>.Update.Set(p => p.Sku, sku));
    //  }
    //
    //  if (request.Price is not null)
    //  {
    //      updates.Add(Builders<Product>.Update.Set(p => p.Price, request.Price.Value));
    //  }
    //
    //  if (request.CategoryId is not null)
    //  {
    //      await EnsureCategoryExistsAsync(request.CategoryId, ct);
    //      updates.Add(Builders<Product>.Update.Set(p => p.CategoryId, request.CategoryId));
    //  }
    //
    //  // Assign only what the request supplied, exactly as above.
    //  if (request.Name is not null)        product.Name = request.Name;
    //  if (request.Description is not null) product.Description = request.Description;
    //
    //  // A translatable property the caller did NOT supply still holds the text the read served.
    //  // Declare it, or the next save files that text as a translation in the request's language —
    //  // quietly turning the English it came from into Arabic. The EF provider does this from
    //  // IsModified; here it is the controller's job, and this is what MarkAsBaseLanguageValues is
    //  // for.
    //  if (request.Name is null)        multiLingual.For(product).MarkAsBaseLanguageValues(p => p.Name);
    //  if (request.Description is null) multiLingual.For(product).MarkAsBaseLanguageValues(p => p.Description);
    //
    //  // The translatable fields go into the DOCUMENT only when the request's language is the base
    //  // one, because that field is where the base language lives. In any other language they
    //  // belong in translation documents — writing them here would overwrite the English — so the
    //  // $set deliberately leaves them out and SaveAsync files them instead. Without a base
    //  // language configured they are unmapped entirely and this branch never runs.
    //  if (multiLingual.Languages.IsBaseLanguage(multiLingual.CurrentLanguage.Code))
    //  {
    //      if (request.Name is not null)        updates.Add(Builders<Product>.Update.Set(p => p.Name, request.Name));
    //      if (request.Description is not null) updates.Add(Builders<Product>.Update.Set(p => p.Description, request.Description));
    //  }
    //
    //  if (updates.Count > 0)
    //  {
    //      await Products.UpdateOneAsync(
    //          p => p.Id == id, Builders<Product>.Update.Combine(updates), cancellationToken: ct);
    //  }
    //
    //  await multiLingual.For(product).SaveAsync(ct);
    //
    //  // Re-read, as above, so the response shows what is stored for the requested language.
    //  var saved = await Products.Find(p => p.Id == id).FirstOrDefaultAsync(ct);
    //  await multiLingual.LocalizeAsync([saved!], ct);
    //
    //  return Ok(ToResponse(saved!) with { Message = localizer["Products.Updated", saved!.Sku] });

    /// <summary>
    /// Removes one language's stored translation of a product — every translatable property in
    /// that language. Reads in it then fall back, exactly as for a language never written.
    /// <para>
    /// A <c>PUT</c> cannot do this: a <c>null</c> field there means "not supplied". And English,
    /// the base language, is not a translation at all — it is the <c>Products</c> row — so it is
    /// refused with a 409; change it with <c>PUT</c> instead.
    /// </para>
    /// </summary>
    [HttpDelete("{id:int}/translations/{languageCode}")]
    [ProducesResponseType<DeleteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeleteResponse>> ClearTranslation(int id, string languageCode, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("Products.NotFound", id);

        // Unknown language → 404, base language → 409: the library refuses both before anything
        // is queued, and MultiLingualExceptionHandler turns them into problem details.
        multiLingual.For(product).Clear(languageCode);
        await db.SaveChangesAsync(ct);

        var code = LanguageCodes.Normalize(languageCode);
        return Ok(new DeleteResponse(product.Id, localizer["Products.TranslationCleared", product.Sku, code]));
    }

    //  MongoDB — ClearTranslation. Clear() queues the removal and the same rules apply (unknown
    //  language → 404, base language → 409); there is simply no SaveChanges for it to ride on, so
    //  SaveAsync applies it. The document itself is untouched.
    //
    //  var product = await Products.Find(p => p.Id == id).FirstOrDefaultAsync(ct)
    //      ?? throw new NotFoundException("Products.NotFound", id);
    //
    //  multiLingual.For(product).Clear(languageCode);
    //  await multiLingual.For(product).SaveAsync(ct);
    //
    //  var code = LanguageCodes.Normalize(languageCode);
    //  return Ok(new DeleteResponse(product.Id, localizer["Products.TranslationCleared", product.Sku, code]));

    /// <summary>
    /// Deletes every product in a category with one bulk statement, then removes the translation
    /// rows that leaves behind.
    /// <para>
    /// <c>ExecuteDelete</c> never passes through <c>SaveChanges</c>, so the interceptor that
    /// removes a deleted entity's translations never sees it. <c>PurgeOrphanedTranslationsAsync</c>
    /// deletes, in the database, every translation whose entity is gone — here straight after the
    /// bulk delete; in an application with many such deletes, on a schedule instead.
    /// </para>
    /// </summary>
    /// <param name="categoryId">The category whose products to delete.</param>
    /// <param name="ct">Cancellation.</param>
    [HttpDelete]
    [ProducesResponseType<BulkDeleteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BulkDeleteResponse>> DeleteByCategory(
        [FromQuery, System.ComponentModel.DataAnnotations.Required] int? categoryId,
        CancellationToken ct)
    {
        var deleted = await db.Products.Where(p => p.CategoryId == categoryId).ExecuteDeleteAsync(ct);
        var purged  = await db.PurgeOrphanedTranslationsAsync(ct);

        return Ok(new BulkDeleteResponse(deleted, purged, localizer["Products.BulkDeleted", deleted, purged]));
    }

    //  MongoDB — DeleteByCategory. There is no PurgeOrphanedTranslationsAsync to fall back on: it
    //  works by joining Translations against each entity's own table, which a driver query cannot
    //  do. So collect the keys BEFORE deleting the documents and remove their translations
    //  explicitly — ITranslationWriter.DeleteEntitiesAsync is that one call, for any number of
    //  entities. Once the documents are gone, nothing can tell you which keys they had.
    //
    //  var doomed = await Products.Find(p => p.CategoryId == categoryId)
    //      .Project(p => p.Id)
    //      .ToListAsync(ct);
    //
    //  var deleted = (int)(await Products.DeleteManyAsync(p => p.CategoryId == categoryId, ct)).DeletedCount;
    //
    //  var purged = await writer.DeleteEntitiesAsync(
    //      MultiLingualMetadata.EntityNameFor(typeof(Product)),
    //      doomed.Select(key => EntityKey.ToKeyString(key)).ToArray(),
    //      ct);
    //
    //  return Ok(new BulkDeleteResponse(deleted, purged, localizer["Products.BulkDeleted", deleted, purged]));

    /// <summary>
    /// Deletes a product. Its translation rows are removed by the save interceptor in the same
    /// transaction — the controller does not mention translations at all.
    /// </summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType<DeleteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeleteResponse>> Delete(int id, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("Products.NotFound", id);

        db.Products.Remove(product);
        await db.SaveChangesAsync(ct);

        return Ok(new DeleteResponse(product.Id, localizer["Products.Deleted", product.Sku]));
    }

    //  MongoDB — Delete. The save interceptor removes a deleted entity's translations for you; here
    //  it is one more call. Outside a transaction the two are separate writes, so a failure between
    //  them leaves translations whose entity is gone — harmless (nothing reads them) but worth a
    //  session if it matters. See docs/adding-a-provider.md.
    //
    //  var product = await Products.Find(p => p.Id == id).FirstOrDefaultAsync(ct)
    //      ?? throw new NotFoundException("Products.NotFound", id);
    //
    //  await Products.DeleteOneAsync(p => p.Id == id, ct);
    //  await multiLingual.For(product).DeleteAsync(ct);
    //
    //  return Ok(new DeleteResponse(product.Id, localizer["Products.Deleted", product.Sku]));

    private async Task EnsureCategoryExistsAsync(int? categoryId, CancellationToken ct)
    {
        if (categoryId is null)
        {
            return;
        }

        if (!await db.Categories.AnyAsync(c => c.Id == categoryId, ct))
        {
            throw new ValidationException("Categories.NotFound", categoryId);
        }
    }

    //  MongoDB — EnsureCategoryExistsAsync. Not translatable, so nothing library-specific.
    //
    //  if (categoryId is null) return;
    //
    //  if (!await Categories.Find(c => c.Id == categoryId).AnyAsync(ct))
    //      throw new ValidationException("Categories.NotFound", categoryId);

    //  MongoDB — NextIdAsync. Only needed because these entities keep an `int` key, which the server
    //  does not generate; the counters collection is the standard pattern for one. A model using
    //  ObjectId deletes this and lets the driver assign the id during the insert — the library
    //  treats ObjectId.Empty as "not saved yet", so a SaveAsync before that insert is refused
    //  rather than filing translations under an empty id.
    //
    //  private async Task<int> NextIdAsync(string counter, CancellationToken ct)
    //  {
    //      var counters = database.GetCollection<BsonDocument>("counters");
    //
    //      var updated = await counters.FindOneAndUpdateAsync(
    //          Builders<BsonDocument>.Filter.Eq("_id", counter),
    //          Builders<BsonDocument>.Update.Inc("value", 1),
    //          new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
    //          ct);
    //
    //      return updated["value"].AsInt32;
    //  }

    private ProductResponse ToResponse(Product product) =>
        new(product.Id,
            product.Sku,
            product.Price,
            product.CategoryId,
            product.Name,
            product.Description,
            multiLingual.CurrentLanguage.Code);
}
