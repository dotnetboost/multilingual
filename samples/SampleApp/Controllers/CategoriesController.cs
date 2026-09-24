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
    // ─────────────────────────────────────────────────────────────────────────
    //  The same controller on MongoDB
    //
    //  As in ProductsController — read that file's preamble first; it explains the three jobs the
    //  EF Core interceptors do that become the controller's here, and the constructor to swap in.
    //  For this controller:
    //
    //    private IMongoCollection<Category> Categories => database.GetCollection<Category>("categories");
    //
    //  and the usings become
    //
    //    + using DotNetBoost.MultiLingual.Core;   // BaseLanguageRequiredException, in Create
    //    + using MongoDB.Driver;
    //    - using DotNetBoost.MultiLingual.EntityFrameworkCore;
    //    - using Microsoft.EntityFrameworkCore;
    //    - using SampleApp.Data;
    //
    //  Category's translations come from a CLASS-LEVEL [MultiLingual], and that changes nothing
    //  below: the engine asks MultiLingualMetadata which properties are translatable, and every
    //  provider reads the same answer. Class-level or property-level is invisible from here.
    // ─────────────────────────────────────────────────────────────────────────

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

    //  MongoDB — GetAll. DisplayOrder is an ordinary field, so unlike the products list this sort
    //  stays in the database; only the localizing is added.
    //
    //  var categories = await Categories
    //      .Find(Builders<Category>.Filter.Empty)
    //      .SortBy(c => c.DisplayOrder)
    //      .ThenBy(c => c.Id)
    //      .ToListAsync(ct);
    //
    //  await multiLingual.LocalizeAsync(categories, ct);
    //
    //  return Ok(categories.Select(ToResponse).ToList());

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

    //  MongoDB — Get.
    //
    //  var category = await Categories.Find(c => c.Id == id).FirstOrDefaultAsync(ct)
    //      ?? throw new NotFoundException("Categories.NotFound", id);
    //
    //  await multiLingual.LocalizeAsync([category], ct);
    //
    //  return Ok(ToResponse(category));

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

    //  MongoDB — GetTranslations. Same manager call; only the load differs, and no localizing is
    //  needed to read every stored language.
    //
    //  var category = await Categories.Find(c => c.Id == id).FirstOrDefaultAsync(ct)
    //      ?? throw new NotFoundException("Categories.NotFound", id);
    //
    //  var stored = await multiLingual.For(category).GetAllAsync(ct);
    //
    //  return Ok(new CategoryTranslationsResponse(category.Id, category.Slug, stored));

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

    //  MongoDB — Create. Same two additions as ProductsController.Create: the base-language rule
    //  the interceptor would enforce, and SaveAsync after the document exists.
    //
    //  if (!multiLingual.Languages.IsBaseLanguage(multiLingual.CurrentLanguage.Code))
    //  {
    //      throw new BaseLanguageRequiredException(
    //          nameof(Category), multiLingual.Languages.BaseLanguage!, multiLingual.CurrentLanguage.Code);
    //  }
    //
    //  var slug = request.Slug.Trim();
    //  if (await Categories.Find(c => c.Slug == slug).AnyAsync(ct))
    //  {
    //      throw new ConflictException("Categories.SlugExists", slug);
    //  }
    //
    //  var category = new Category
    //  {
    //      Id = await NextIdAsync("categories", ct),   // see ProductsController
    //      Slug = slug,
    //      DisplayOrder = request.DisplayOrder,
    //      Name = request.Name,
    //      Description = request.Description
    //  };
    //
    //  await Categories.InsertOneAsync(category, cancellationToken: ct);
    //  await multiLingual.For(category).SaveAsync(ct);
    //
    //  var created = ToResponse(category) with { Message = localizer["Categories.Created", category.Slug] };
    //
    //  return CreatedAtAction(nameof(Get), new { id = category.Id }, created);

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

    //  MongoDB — Update. See ProductsController.Update for why the translatable fields are held out
    //  of the $set in any language but the base one, and why the untouched ones are declared.
    //
    //  var category = await Categories.Find(c => c.Id == id).FirstOrDefaultAsync(ct)
    //      ?? throw new NotFoundException("Categories.NotFound", id);
    //
    //  await multiLingual.LocalizeAsync([category], ct);
    //
    //  var updates = new List<UpdateDefinition<Category>>();
    //
    //  if (request.Slug is not null)
    //  {
    //      var slug = request.Slug.Trim();
    //      if (await Categories.Find(c => c.Slug == slug && c.Id != id).AnyAsync(ct))
    //      {
    //          throw new ConflictException("Categories.SlugExists", slug);
    //      }
    //
    //      updates.Add(Builders<Category>.Update.Set(c => c.Slug, slug));
    //  }
    //
    //  if (request.DisplayOrder is not null)
    //  {
    //      updates.Add(Builders<Category>.Update.Set(c => c.DisplayOrder, request.DisplayOrder.Value));
    //  }
    //
    //  if (request.Name is not null)        category.Name = request.Name;
    //  if (request.Description is not null) category.Description = request.Description;
    //
    //  if (request.Name is null)        multiLingual.For(category).MarkAsBaseLanguageValues(c => c.Name);
    //  if (request.Description is null) multiLingual.For(category).MarkAsBaseLanguageValues(c => c.Description);
    //
    //  if (multiLingual.Languages.IsBaseLanguage(multiLingual.CurrentLanguage.Code))
    //  {
    //      if (request.Name is not null)        updates.Add(Builders<Category>.Update.Set(c => c.Name, request.Name));
    //      if (request.Description is not null) updates.Add(Builders<Category>.Update.Set(c => c.Description, request.Description));
    //  }
    //
    //  if (updates.Count > 0)
    //  {
    //      await Categories.UpdateOneAsync(
    //          c => c.Id == id, Builders<Category>.Update.Combine(updates), cancellationToken: ct);
    //  }
    //
    //  await multiLingual.For(category).SaveAsync(ct);
    //
    //  var saved = await Categories.Find(c => c.Id == id).FirstOrDefaultAsync(ct);
    //  await multiLingual.LocalizeAsync([saved!], ct);
    //
    //  return Ok(ToResponse(saved!) with { Message = localizer["Categories.Updated", saved!.Slug] });

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

    //  MongoDB — Delete. Deleting a category leaves its products' CategoryId dangling: EF Core does
    //  that with OnDelete(SetNull) in AppDbContext, and MongoDB has no such declaration, so do it
    //  here. The category's own translations go with For(category).DeleteAsync() — the products'
    //  translations are untouched, since the products themselves survive.
    //
    //  var category = await Categories.Find(c => c.Id == id).FirstOrDefaultAsync(ct)
    //      ?? throw new NotFoundException("Categories.NotFound", id);
    //
    //  await database.GetCollection<Product>("products").UpdateManyAsync(
    //      p => p.CategoryId == id,
    //      Builders<Product>.Update.Set(p => p.CategoryId, null),
    //      cancellationToken: ct);
    //
    //  await Categories.DeleteOneAsync(c => c.Id == id, ct);
    //  await multiLingual.For(category).DeleteAsync(ct);
    //
    //  return Ok(new DeleteResponse(category.Id, localizer["Categories.Deleted", category.Slug]));

    private CategoryResponse ToResponse(Category category) =>
        new(category.Id,
            category.Slug,
            category.DisplayOrder,
            category.Name,
            category.Description,
            multiLingual.CurrentLanguage.Code);
}
