using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    ITranslationHydrator hydrator,
    ITranslationManager translations,
    ILanguageCatalog languages,
    MultiLingualOptions options,
    ILanguageContext language)
    : ControllerBase
{
    /// <summary>
    /// Lists products, each resolved to the requested language
    /// (<c>Accept-Language</c>, then the system default).
    /// <para>
    /// <c>search</c> and <c>sort</c> work on the TRANSLATED name — the one this response shows —
    /// in the database. A plain <c>Where(p =&gt; p.Name.Contains(…))</c> would only ever search
    /// the English column; <c>WhereTranslation</c> / <c>OrderByTranslation</c> search and sort
    /// the requested language, falling back exactly as the read does.
    /// </para>
    /// </summary>
    /// <param name="categoryId">Only products in this category.</param>
    /// <param name="search">Part of the name, in the requested language (case-insensitive for ASCII on SQLite).</param>
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
            var pattern = $"%{search.Trim()}%";
            query = query.WhereTranslation(db, p => p.Name, name => name != null && EF.Functions.Like(name, pattern));
        }

        query = sort?.Trim().ToLowerInvariant() switch
        {
            null or "" or "id" => query.OrderBy(p => p.Id),
            "name"             => query.OrderByTranslation(db, p => p.Name).ThenBy(p => p.Id),
            "-name"            => query.OrderByTranslationDescending(db, p => p.Name).ThenBy(p => p.Id),
            _                  => throw new ValidationException("Products.UnknownSort", sort)
        };

        // One query for the products, one for all their translations.
        var products = await query.ToLocalizedListAsync(hydrator, ct);

        return Ok(products.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Lists products straight into a small DTO, with the category's name alongside — in the
    /// requested language, in one query.
    /// <para>
    /// This is the projection shape most read endpoints use, and a plain <c>Select</c> cannot do
    /// it here: <c>p.Name</c> and <c>p.Category.Name</c> are English columns in this sample's
    /// base-language mode, so it would answer every language in English. <c>SelectTranslated</c>
    /// reads each translatable property in the request's language instead, navigations included.
    /// </para>
    /// </summary>
    [HttpGet("summaries")]
    [ProducesResponseType<IReadOnlyList<ProductSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProductSummaryResponse>>> GetSummaries(CancellationToken ct)
    {
        var languageCode = language.Current.Code;

        var summaries = await db.Products
            .AsNoTracking()
            .OrderBy(p => p.Id)
            .SelectTranslated(db, p => new ProductSummaryResponse(p.Id, p.Sku, p.Price, p.Name, p.Category!.Name, languageCode))
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
            .FirstLocalizedOrDefaultAsync(hydrator, ct)
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

        var stored = await translations.GetAllAsync(product, ct);

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

        return CreatedAtAction(nameof(Get), new { id = product.Id }, ToResponse(product));
    }

    /// <summary>
    /// Updates a product. <c>Name</c>/<c>Description</c> are written for the request language
    /// (<c>Accept-Language</c>) only; other languages are untouched. With <c>en</c> that means the
    /// columns on <c>Products</c>; with any other language, a row in <c>Translations</c>.
    /// <para>
    /// Every field is optional and a <c>null</c> means "leave it alone", which is why the entity
    /// is loaded WITHOUT hydration and why only supplied values are assigned below.
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
        // a property nobody assigns keeps the value the column was loaded with, which the engine
        // recognises as borrowed and never files as a translation.
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
            .FirstLocalizedOrDefaultAsync(hydrator, ct);

        return Ok(ToResponse(saved!));
    }

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
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ClearTranslation(int id, string languageCode, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("Products.NotFound", id);

        var target = await languages.FindByCodeAsync(languageCode, ct)
            ?? throw new NotFoundException("Languages.NotFound", languageCode);

        if (options.IsBaseLanguage(target.Code))
        {
            throw new ConflictException("Products.CannotClearBaseLanguage", target.Code);
        }

        translations.Clear(product, target.Code);
        await db.SaveChangesAsync(ct);

        return NoContent();
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

        return Ok(new BulkDeleteResponse(deleted, purged));
    }

    /// <summary>
    /// Deletes a product. Its translation rows are removed by the save interceptor in the same
    /// transaction — the controller does not mention translations at all.
    /// </summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException("Products.NotFound", id);

        db.Products.Remove(product);
        await db.SaveChangesAsync(ct);

        return NoContent();
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
            language.Current.Code);
}
