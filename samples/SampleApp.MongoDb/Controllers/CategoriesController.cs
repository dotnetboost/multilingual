using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using MongoDB.Driver;
using SampleApp.Dtos;
using SampleApp.Infrastructure;
using SampleApp.MongoDb.Data;
using SampleApp.MongoDb.Domain;

namespace SampleApp.MongoDb.Controllers;

/// <summary>
/// Identical code shape to <see cref="ProductsController"/>, over MongoDB — which is the same point
/// the EF Core sample's <c>CategoriesController</c> makes. <c>Category</c> gets its translations from
/// a CLASS-LEVEL <c>[MultiLingual]</c>, and nothing here knows that: every provider asks
/// <c>MultiLingualMetadata</c> the same question.
/// <para>
/// Read <see cref="ProductsController"/> first — its comments explain the three jobs the EF Core
/// interceptors do that are written out here, and its <c>Update</c> explains the base-language rules.
/// </para>
/// </summary>
[ApiController]
[Route("api/categories")]
[Produces("application/json")]
public sealed class CategoriesController(
    IMongoCollection<Category> categories,
    IMongoCollection<Product> products,
    IdSequence ids,
    IMultiLingualManager multiLingual,
    IStringLocalizer<SharedResource> localizer)
    : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CategoryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CategoryResponse>>> GetAll(CancellationToken ct)
    {
        // DisplayOrder is an ordinary field, so unlike the products list this sort stays in the
        // database; only the localizing is added.
        var found = await categories
            .Find(Builders<Category>.Filter.Empty)
            .SortBy(c => c.DisplayOrder)
            .ThenBy(c => c.Id)
            .ToListAsync(ct);

        await multiLingual.LocalizeAsync(found, ct);

        return Ok(found.Select(ToResponse).ToList());
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<CategoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CategoryResponse>> Get(int id, CancellationToken ct)
    {
        var category = await FindAsync(id, ct);

        await multiLingual.LocalizeAsync([category], ct);

        return Ok(ToResponse(category));
    }

    [HttpGet("{id:int}/translations")]
    [ProducesResponseType<CategoryTranslationsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CategoryTranslationsResponse>> GetTranslations(int id, CancellationToken ct)
    {
        var category = await FindAsync(id, ct);

        var stored = await multiLingual.For(category).GetAllAsync(ct);

        return Ok(new CategoryTranslationsResponse(category.Id, category.Slug, stored));
    }

    [HttpPost]
    [ProducesResponseType<CategoryResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CategoryResponse>> Create(
        [FromBody] CreateCategoryRequest request,
        CancellationToken ct)
    {
        // As in ProductsController.Create: the interceptor's base-language rule, made explicit.
        if (!multiLingual.Languages.IsBaseLanguage(multiLingual.CurrentLanguage.Code))
        {
            throw new BaseLanguageRequiredException(
                nameof(Category), multiLingual.Languages.BaseLanguage!, multiLingual.CurrentLanguage.Code);
        }

        var slug = request.Slug.Trim();
        if (await categories.Find(c => c.Slug == slug).AnyAsync(ct))
        {
            throw new ConflictException("Categories.SlugExists", slug);
        }

        var category = new Category
        {
            Id = await ids.NextAsync("categories", ct),
            Slug = slug,
            DisplayOrder = request.DisplayOrder,
            Name = request.Name,
            Description = request.Description
        };

        await categories.InsertOneAsync(category, cancellationToken: ct);
        await multiLingual.For(category).SaveAsync(ct);

        var created = ToResponse(category) with { Message = localizer["Categories.Created", category.Slug] };

        return CreatedAtAction(nameof(Get), new { id = category.Id }, created);
    }

    [HttpPut("{id:int}")]
    [ProducesResponseType<CategoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CategoryResponse>> Update(
        int id,
        [FromBody] UpdateCategoryRequest request,
        CancellationToken ct)
    {
        var category = await FindAsync(id, ct);

        await multiLingual.LocalizeAsync([category], ct);

        var updates = new List<UpdateDefinition<Category>>();

        if (request.Slug is not null)
        {
            var slug = request.Slug.Trim();
            if (await categories.Find(c => c.Slug == slug && c.Id != id).AnyAsync(ct))
            {
                throw new ConflictException("Categories.SlugExists", slug);
            }

            updates.Add(Builders<Category>.Update.Set(c => c.Slug, slug));
        }

        if (request.DisplayOrder is not null)
        {
            updates.Add(Builders<Category>.Update.Set(c => c.DisplayOrder, request.DisplayOrder.Value));
        }

        // Only what the request supplied, and the two base-language rules — see
        // ProductsController.Update for why each is needed.
        if (request.Name is not null)
        {
            category.Name = request.Name;
        }
        else
        {
            multiLingual.For(category).MarkAsBaseLanguageValues(c => c.Name);
        }

        if (request.Description is not null)
        {
            category.Description = request.Description;
        }
        else
        {
            multiLingual.For(category).MarkAsBaseLanguageValues(c => c.Description);
        }

        if (multiLingual.Languages.IsBaseLanguage(multiLingual.CurrentLanguage.Code))
        {
            if (request.Name is not null)
            {
                updates.Add(Builders<Category>.Update.Set(c => c.Name, request.Name));
            }

            if (request.Description is not null)
            {
                updates.Add(Builders<Category>.Update.Set(c => c.Description, request.Description));
            }
        }

        if (updates.Count > 0)
        {
            await categories.UpdateOneAsync(
                c => c.Id == id, Builders<Category>.Update.Combine(updates), cancellationToken: ct);
        }

        await multiLingual.For(category).SaveAsync(ct);

        var saved = await categories.Find(c => c.Id == id).FirstAsync(ct);
        await multiLingual.LocalizeAsync([saved], ct);

        return Ok(ToResponse(saved) with { Message = localizer["Categories.Updated", saved.Slug] });
    }

    /// <summary>
    /// Deletes a category. Its products are kept, with no category — which the EF Core sample gets
    /// from <c>OnDelete(DeleteBehavior.SetNull)</c> in its model. MongoDB has no such declaration, so
    /// the products are updated here. Their own translations are untouched, since the products
    /// survive; the category's go with <c>For(category).DeleteAsync()</c>.
    /// </summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType<DeleteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeleteResponse>> Delete(int id, CancellationToken ct)
    {
        var category = await FindAsync(id, ct);

        await products.UpdateManyAsync(
            p => p.CategoryId == id,
            Builders<Product>.Update.Set(p => p.CategoryId, null),
            cancellationToken: ct);

        await categories.DeleteOneAsync(c => c.Id == id, ct);
        await multiLingual.For(category).DeleteAsync(ct);

        return Ok(new DeleteResponse(category.Id, localizer["Categories.Deleted", category.Slug]));
    }

    private async Task<Category> FindAsync(int id, CancellationToken ct) =>
        await categories.Find(c => c.Id == id).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Categories.NotFound", id);

    private CategoryResponse ToResponse(Category category) =>
        new(category.Id,
            category.Slug,
            category.DisplayOrder,
            category.Name,
            category.Description,
            multiLingual.CurrentLanguage.Code);
}
