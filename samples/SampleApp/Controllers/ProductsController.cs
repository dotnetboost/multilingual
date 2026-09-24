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
    // Without a change tracker — MongoDB, Dapper — the same work is explicit: localize after a
    // query, then save translations once the document exists. samples/SampleApp.MongoDb is this
    // controller's shape over MongoDB, runnable, and docs/mongodb.md lists what differs. The notes
    // on GetAll, Create, Update and DeleteByCategory below are the four that matter.

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

    // No change tracker, no translated query: search and sort on TRANSLATED text work by rewriting
    // the LINQ query, so they are EF Core only. A MongoDB controller filters on what it stores,
    // localizes, then sorts in memory — see samples/SampleApp.MongoDb.

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

    // The 409 above comes from the save interceptor. Nothing intercepts a driver insert, so a
    // MongoDB controller makes this check itself — see samples/SampleApp.MongoDb — or Arabic text
    // ends up in the field that is supposed to hold English.

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

    // The two lines above are the interceptor reading IsModified for you, and without a change
    // tracker they become the caller's job — the easiest thing to get wrong in base-language mode.
    // A write in another language must not touch the base column, and a translatable property the
    // caller did not supply still holds what the READ served, so it needs
    // For(entity).MarkAsBaseLanguageValues(…) or that English is filed as an Arabic translation.
    // samples/SampleApp.MongoDb/Program.cs does both explicitly.

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

    // PurgeOrphanedTranslationsAsync is EF Core only: it works by joining Translations against each
    // entity's own table. Elsewhere, collect the keys BEFORE deleting and pass them to
    // ITranslationWriter.DeleteEntitiesAsync — once the rows are gone, nothing knows what they were.

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

    private ProductResponse ToResponse(Product product) =>
        new(product.Id,
            product.Sku,
            product.Price,
            product.CategoryId,
            product.Name,
            product.Description,
            multiLingual.CurrentLanguage.Code);
}
