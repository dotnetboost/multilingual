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
/// Identical code shape to <see cref="ProductsController"/> — which is the point. Category
/// gets its translations from a CLASS-LEVEL <c>[MultiLingual]</c> and needed no extra service,
/// repository or mapping code to support it.
/// </summary>
[ApiController]
[Route("api/categories")]
[Produces("application/json")]
public sealed class CategoriesController(
    AppDbContext db,
    IMultiLingualManager multiLingual,
    IStringLocalizer<SharedResource> localizer)
    : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CategoryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CategoryResponse>>> GetAll(CancellationToken ct)
    {
        var categories = await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Id)
            .ToListAsync(ct);

        return Ok(categories.Select(ToResponse).ToList());
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<CategoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CategoryResponse>> Get(int id, CancellationToken ct)
    {
        var category = await db.Categories
            .AsNoTracking()
            .Where(c => c.Id == id)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Categories.NotFound", id);

        return Ok(ToResponse(category));
    }

    [HttpGet("{id:int}/translations")]
    [ProducesResponseType<CategoryTranslationsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CategoryTranslationsResponse>> GetTranslations(int id, CancellationToken ct)
    {
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundException("Categories.NotFound", id);

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
        var slug = request.Slug.Trim();
        if (await db.Categories.AnyAsync(c => c.Slug == slug, ct))
        {
            throw new ConflictException("Categories.SlugExists", slug);
        }

        var category = new Category
        {
            Slug = slug,
            DisplayOrder = request.DisplayOrder,
            Name = request.Name,
            Description = request.Description
        };

        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);

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
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundException("Categories.NotFound", id);

        if (request.Slug is not null)
        {
            var slug = request.Slug.Trim();
            if (await db.Categories.AnyAsync(c => c.Slug == slug && c.Id != id, ct))
            {
                throw new ConflictException("Categories.SlugExists", slug);
            }

            category.Slug = slug;
        }

        if (request.DisplayOrder is not null)
        {
            category.DisplayOrder = request.DisplayOrder.Value;
        }

        // Only what the request supplied — see ProductsController.Update for why.
        if (request.Name is not null)
        {
            category.Name = request.Name;
        }

        if (request.Description is not null)
        {
            category.Description = request.Description;
        }

        await db.SaveChangesAsync(ct);

        db.ChangeTracker.Clear();
        var saved = await db.Categories
            .AsNoTracking()
            .Where(c => c.Id == id)
            .FirstOrDefaultAsync(ct);

        return Ok(ToResponse(saved!) with { Message = localizer["Categories.Updated", saved!.Slug] });
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType<DeleteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeleteResponse>> Delete(int id, CancellationToken ct)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundException("Categories.NotFound", id);

        db.Categories.Remove(category);
        await db.SaveChangesAsync(ct);

        return Ok(new DeleteResponse(category.Id, localizer["Categories.Deleted", category.Slug]));
    }

    private CategoryResponse ToResponse(Category category) =>
        new(category.Id,
            category.Slug,
            category.DisplayOrder,
            category.Name,
            category.Description,
            multiLingual.CurrentLanguage.Code);
}
