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
    ILanguageContext language)
    : ControllerBase
{
    /// <summary>
    /// Lists products, each resolved to the requested language
    /// (<c>Accept-Language</c>, then the system default).
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ProductResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProductResponse>>> GetAll(
        [FromQuery] int? categoryId,
        CancellationToken ct)
    {
        var query = db.Products.AsNoTracking().OrderBy(p => p.Id).AsQueryable();

        if (categoryId is not null)
        {
            query = query.Where(p => p.CategoryId == categoryId);
        }

        // One query for the products, one for all their translations.
        var products = await query.ToLocalizedListAsync(hydrator, ct);

        return Ok(products.Select(ToResponse).ToList());
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
            ?? throw new NotFoundException($"Product {id} was not found.");

        return Ok(ToResponse(product));
    }

    /// <summary>Every stored language for one product — the easiest way to verify a write.</summary>
    [HttpGet("{id:int}/translations")]
    [ProducesResponseType<ProductTranslationsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductTranslationsResponse>> GetTranslations(int id, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException($"Product {id} was not found.");

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
            throw new ConflictException($"A product with SKU '{sku}' already exists.");
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
            ?? throw new NotFoundException($"Product {id} was not found.");

        if (request.Sku is not null)
        {
            var sku = request.Sku.Trim();
            if (await db.Products.AnyAsync(p => p.Sku == sku && p.Id != id, ct))
            {
                throw new ConflictException($"A product with SKU '{sku}' already exists.");
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
    /// Deletes a product. Its translation rows are removed by the save interceptor in the same
    /// transaction — the controller does not mention translations at all.
    /// </summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new NotFoundException($"Product {id} was not found.");

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
            throw new ValidationException($"Category {categoryId} was not found.");
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
