using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using MongoDB.Driver;
using SampleApp.Dtos;
using SampleApp.Infrastructure;
using SampleApp.MongoDb.Data;
using SampleApp.MongoDb.Domain;

namespace SampleApp.MongoDb.Controllers;

/// <summary>
/// The EF Core sample's <c>ProductsController</c>, endpoint for endpoint, over MongoDB. Same routes,
/// same request and response types — literally the same DTO files — so
/// <c>clients/dashboard</c> can be pointed at either API.
/// <para>
/// What differs is that there is no change tracker to hook, so three things the EF Core interceptors
/// do quietly are written out here:
/// </para>
/// <list type="number">
///   <item><b>Localize after a query.</b> A driver query fills a document's own fields; the
///   translatable ones arrive null until <c>LocalizeAsync</c> fills them.</item>
///   <item><b>Save translations after the document</b>, so its key exists —
///   <c>For(entity).SaveAsync()</c> where the EF sample relies on <c>SaveChanges</c>.</item>
///   <item><b>Apply the base-language rules yourself.</b> See <see cref="Create"/> and
///   <see cref="Update"/>; they are the two easiest things to get wrong.</item>
/// </list>
/// <para>
/// And one thing is simply not available: filtering and sorting on TRANSLATED text inside the
/// database. See <see cref="GetAll"/>. docs/mongodb.md covers all four.
/// </para>
/// </summary>
[ApiController]
[Route("api/products")]
[Produces("application/json")]
public sealed class ProductsController(
    IMongoCollection<Product> products,
    IMongoCollection<Category> categories,
    IdSequence ids,
    IMultiLingualManager multiLingual,
    ITranslationWriter writer,
    IStringLocalizer<SharedResource> localizer)
    : ControllerBase
{
    /// <summary>
    /// Lists products, each resolved to the requested language
    /// (<c>Accept-Language</c>, then the system default).
    /// <para>
    /// <c>categoryId</c> is an ordinary field filter and stays in the database. <c>search</c> and
    /// <c>sort</c> are on the TRANSLATED name, and that is the one thing the EF Core provider does
    /// which no driver query can: it rewrites the LINQ query so the comparison happens in SQL. Here
    /// they run after <c>LocalizeAsync</c> instead. Fine for a page of results; a large collection
    /// wants a <c>$lookup</c> pipeline against the translations collection, or a denormalised
    /// per-language field.
    /// </para>
    /// </summary>
    /// <param name="categoryId">Only products in this category.</param>
    /// <param name="search">Part of the name, in the requested language (case-insensitive).</param>
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
        var filter = Builders<Product>.Filter.Empty;

        if (categoryId is not null)
        {
            filter &= Builders<Product>.Filter.Eq(p => p.CategoryId, categoryId);
        }

        var found = await products.Find(filter).SortBy(p => p.Id).ToListAsync(ct);

        // Name and Description are null until this line.
        await multiLingual.LocalizeAsync(found, ct);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            found = found
                .Where(p => p.Name is not null && p.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        found = sort?.Trim().ToLowerInvariant() switch
        {
            null or "" or "id" => found.OrderBy(p => p.Id).ToList(),
            "name"             => found.OrderBy(p => p.Name, StringComparer.CurrentCulture).ThenBy(p => p.Id).ToList(),
            "-name"            => found.OrderByDescending(p => p.Name, StringComparer.CurrentCulture).ThenBy(p => p.Id).ToList(),
            _                  => throw new ValidationException("Products.UnknownSort", sort)
        };

        return Ok(found.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Lists products into a small DTO with the category's name alongside, in the requested language.
    /// <para>
    /// The EF Core sample reads both in one statement, through the <c>Category</c> navigation. A
    /// document has no navigation, so each collection is read and localized on its own and joined
    /// here. <c>LocalizeAsync</c> batches per entity type, so this is two translation queries in
    /// total however many products come back — not one per row.
    /// </para>
    /// </summary>
    [HttpGet("summaries")]
    [ProducesResponseType<IReadOnlyList<ProductSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProductSummaryResponse>>> GetSummaries(CancellationToken ct)
    {
        var languageCode = multiLingual.CurrentLanguage.Code;

        var found = await products.Find(Builders<Product>.Filter.Empty).SortBy(p => p.Id).ToListAsync(ct);
        var all   = await categories.Find(Builders<Category>.Filter.Empty).ToListAsync(ct);

        await multiLingual.LocalizeAsync(found, ct);
        await multiLingual.LocalizeAsync(all, ct);

        var names = all.ToDictionary(c => c.Id, c => c.Name);

        return Ok(found
            .Select(p => new ProductSummaryResponse(
                p.Id, p.Sku, p.Price, p.Name,
                p.CategoryId is { } id && names.TryGetValue(id, out var name) ? name : null,
                languageCode))
            .ToList());
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponse>> Get(int id, CancellationToken ct)
    {
        var product = await FindAsync(id, ct);

        await multiLingual.LocalizeAsync([product], ct);

        return Ok(ToResponse(product));
    }

    /// <summary>Every stored language for one product — the easiest way to verify a write.</summary>
    [HttpGet("{id:int}/translations")]
    [ProducesResponseType<ProductTranslationsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductTranslationsResponse>> GetTranslations(int id, CancellationToken ct)
    {
        var product = await FindAsync(id, ct);

        // No localizing needed, and the same call the EF Core sample makes.
        var stored = await multiLingual.For(product).GetAllAsync(ct);

        return Ok(new ProductTranslationsResponse(product.Id, product.Sku, stored));
    }

    /// <summary>
    /// Creates a product. English is the base language, so <c>Name</c>/<c>Description</c> stay in the
    /// document's own fields and the request must carry <c>Accept-Language: en</c>. Add other
    /// languages afterwards with <c>PUT</c>.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductResponse>> Create(
        [FromBody] CreateProductRequest request,
        CancellationToken ct)
    {
        // In base-language mode an entity may only be CREATED in the base language, because every
        // translation hangs off the base value. The EF Core save interceptor enforces that; nothing
        // intercepts a driver insert, so the check is here — without it, an Arabic request would put
        // Arabic text in the field that is supposed to hold English.
        //
        // The same exception EF Core throws, so MultiLingualExceptionHandler still answers 409.
        RequireBaseLanguage();

        await EnsureCategoryExistsAsync(request.CategoryId, ct);

        var sku = request.Sku.Trim();
        if (await products.Find(p => p.Sku == sku).AnyAsync(ct))
        {
            throw new ConflictException("Products.SkuExists", sku);
        }

        var product = new Product
        {
            Id = await ids.NextAsync("products", ct),
            Sku = sku,
            Price = request.Price,
            CategoryId = request.CategoryId,
            Name = request.Name,
            Description = request.Description
        };

        // The document first, so its key exists before anything refers to it. In base-language mode
        // the English text is a mapped field, so the insert stores it and SaveAsync finds nothing to
        // file as a translation. Without a base language the document carries neither, and SaveAsync
        // is what stores the English. Either way the call belongs here: it is also what writes
        // anything an earlier Set(...) queued.
        await products.InsertOneAsync(product, cancellationToken: ct);
        await multiLingual.For(product).SaveAsync(ct);

        // The localizer answers in the request's language, from Resources/{language}.json.
        var created = ToResponse(product) with { Message = localizer["Products.Created", product.Sku] };

        return CreatedAtAction(nameof(Get), new { id = product.Id }, created);
    }

    /// <summary>
    /// Updates a product. <c>Name</c>/<c>Description</c> are written for the request language only;
    /// other languages are untouched. Every field is optional and a <c>null</c> means "leave it
    /// alone".
    /// <para>
    /// This is the method worth reading. The EF Core provider infers two things from
    /// <c>IsModified</c>, and without a change tracker both are the caller's — and both only matter
    /// in base-language mode. The comments below mark them.
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
        var product = await FindAsync(id, ct);

        // Localize BEFORE anything is assigned: the properties then hold what the read served, which
        // is what lets the engine tell that apart from a value the caller authored.
        await multiLingual.LocalizeAsync([product], ct);

        var updates = new List<UpdateDefinition<Product>>();

        if (request.Sku is not null)
        {
            var sku = request.Sku.Trim();
            if (await products.Find(p => p.Sku == sku && p.Id != id).AnyAsync(ct))
            {
                throw new ConflictException("Products.SkuExists", sku);
            }

            updates.Add(Builders<Product>.Update.Set(p => p.Sku, sku));
        }

        if (request.Price is not null)
        {
            updates.Add(Builders<Product>.Update.Set(p => p.Price, request.Price.Value));
        }

        if (request.CategoryId is not null)
        {
            await EnsureCategoryExistsAsync(request.CategoryId, ct);
            updates.Add(Builders<Product>.Update.Set(p => p.CategoryId, request.CategoryId));
        }

        // Only what the request supplied, exactly as in the EF Core sample.
        if (request.Name is not null)
        {
            product.Name = request.Name;
        }
        else
        {
            // (1) A translatable property the caller did NOT supply still holds the text the read
            // served. Declare it, or the next save files that text as a translation in the request's
            // language — quietly turning the English it came from into Arabic.
            multiLingual.For(product).MarkAsBaseLanguageValues(p => p.Name);
        }

        if (request.Description is not null)
        {
            product.Description = request.Description;
        }
        else
        {
            multiLingual.For(product).MarkAsBaseLanguageValues(p => p.Description);
        }

        // (2) The translatable fields go into the DOCUMENT only when the request's language is the
        // base one, because that field is where the base language lives. In any other language they
        // belong in translation documents — writing them here would overwrite the English — so the
        // $set deliberately leaves them out and SaveAsync files them instead. Without a base language
        // they are unmapped entirely and this branch never runs.
        if (multiLingual.Languages.IsBaseLanguage(multiLingual.CurrentLanguage.Code))
        {
            if (request.Name is not null)
            {
                updates.Add(Builders<Product>.Update.Set(p => p.Name, request.Name));
            }

            if (request.Description is not null)
            {
                updates.Add(Builders<Product>.Update.Set(p => p.Description, request.Description));
            }
        }

        if (updates.Count > 0)
        {
            await products.UpdateOneAsync(
                p => p.Id == id, Builders<Product>.Update.Combine(updates), cancellationToken: ct);
        }

        await multiLingual.For(product).SaveAsync(ct);

        // Re-read, so the response shows what is actually stored for the requested language.
        var saved = await products.Find(p => p.Id == id).FirstAsync(ct);
        await multiLingual.LocalizeAsync([saved], ct);

        return Ok(ToResponse(saved) with { Message = localizer["Products.Updated", saved.Sku] });
    }

    /// <summary>
    /// Removes one language's stored translation of a product — every translatable property in that
    /// language. Reads in it then fall back, exactly as for a language never written.
    /// <para>
    /// A <c>PUT</c> cannot do this: a <c>null</c> field there means "not supplied". And English, the
    /// base language, is not a translation at all — it is the document's own field — so it is refused
    /// with a 409.
    /// </para>
    /// </summary>
    [HttpDelete("{id:int}/translations/{languageCode}")]
    [ProducesResponseType<DeleteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeleteResponse>> ClearTranslation(int id, string languageCode, CancellationToken ct)
    {
        var product = await FindAsync(id, ct);

        // Unknown language → 404, base language → 409: the library refuses both before anything is
        // queued. Clear() only queues the removal, so SaveAsync applies it; with EF Core,
        // SaveChanges would.
        multiLingual.For(product).Clear(languageCode);
        await multiLingual.For(product).SaveAsync(ct);

        var code = LanguageCodes.Normalize(languageCode);
        return Ok(new DeleteResponse(product.Id, localizer["Products.TranslationCleared", product.Sku, code]));
    }

    /// <summary>
    /// Deletes every product in a category, then the translations they leave behind.
    /// <para>
    /// The EF Core sample calls <c>PurgeOrphanedTranslationsAsync</c>, which is EF Core only: it
    /// works by joining <c>Translations</c> against each entity's own table, and a driver query
    /// cannot. So collect the keys BEFORE deleting the documents and remove their translations
    /// explicitly — <c>ITranslationWriter.DeleteEntitiesAsync</c> is one call for any number of
    /// entities. Once the documents are gone, nothing can tell you which keys they had.
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
        var doomed = await products.Find(p => p.CategoryId == categoryId)
            .Project(p => p.Id)
            .ToListAsync(ct);

        var deleted = (int)(await products.DeleteManyAsync(p => p.CategoryId == categoryId, ct)).DeletedCount;

        var purged = await writer.DeleteEntitiesAsync(
            MultiLingualMetadata.EntityNameFor(typeof(Product)),
            doomed.Select(key => EntityKey.ToKeyString(key)).ToArray(),
            ct);

        return Ok(new BulkDeleteResponse(deleted, purged, localizer["Products.BulkDeleted", deleted, purged]));
    }

    /// <summary>
    /// Deletes a product and its translations. The EF Core save interceptor does the second half in
    /// the same transaction; here it is one more call.
    /// <para>
    /// Outside a transaction these are two writes, so a failure between them leaves translations
    /// whose document is gone — harmless, since nothing reads them, but a session closes the gap.
    /// See docs/mongodb.md.
    /// </para>
    /// </summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType<DeleteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeleteResponse>> Delete(int id, CancellationToken ct)
    {
        var product = await FindAsync(id, ct);

        await products.DeleteOneAsync(p => p.Id == id, ct);
        await multiLingual.For(product).DeleteAsync(ct);

        return Ok(new DeleteResponse(product.Id, localizer["Products.Deleted", product.Sku]));
    }

    private async Task<Product> FindAsync(int id, CancellationToken ct) =>
        await products.Find(p => p.Id == id).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Products.NotFound", id);

    private async Task EnsureCategoryExistsAsync(int? categoryId, CancellationToken ct)
    {
        if (categoryId is null)
        {
            return;
        }

        if (!await categories.Find(c => c.Id == categoryId).AnyAsync(ct))
        {
            throw new ValidationException("Categories.NotFound", categoryId);
        }
    }

    /// <summary>Refuses a create outside the base language, as the EF Core interceptor does.</summary>
    private void RequireBaseLanguage()
    {
        if (multiLingual.Languages.IsBaseLanguage(multiLingual.CurrentLanguage.Code))
        {
            return;
        }

        throw new BaseLanguageRequiredException(
            nameof(Product), multiLingual.Languages.BaseLanguage!, multiLingual.CurrentLanguage.Code);
    }

    private ProductResponse ToResponse(Product product) =>
        new(product.Id,
            product.Sku,
            product.Price,
            product.CategoryId,
            product.Name,
            product.Description,
            multiLingual.CurrentLanguage.Code);
}
