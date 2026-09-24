using Dapper;
using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SampleApp.Dapper.Data;
using SampleApp.Dapper.Domain;
using SampleApp.Dtos;
using SampleApp.Infrastructure;
using System.Data;

namespace SampleApp.Dapper.Controllers;

/// <summary>
/// Identical code shape to <see cref="ProductsController"/>, over Dapper — which is the same point
/// the EF Core sample's <c>CategoriesController</c> makes. <c>Category</c> gets its translations from
/// a CLASS-LEVEL <c>[MultiLingual]</c>, and nothing here knows that: every provider asks
/// <c>MultiLingualMetadata</c> the same question.
/// <para>
/// Read <see cref="ProductsController"/> first — its comments explain the three jobs the EF Core
/// interceptors do that are written out here, its <c>Update</c> explains the base-language rules,
/// and its remarks explain where the transaction comes from.
/// </para>
/// </summary>
[ApiController]
[Route("api/categories")]
[Produces("application/json")]
public sealed class CategoriesController(
    IDbConnection connection,
    UnitOfWork unitOfWork,
    IMultiLingualManager multiLingual,
    IStringLocalizer<SharedResource> localizer)
    : ControllerBase
{
    private const string CategoryColumns = """ "Id", "Slug", "DisplayOrder", "Name", "Description" """;

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CategoryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CategoryResponse>>> GetAll(CancellationToken ct)
    {
        // DisplayOrder is an ordinary column, so unlike the products list this sort stays in the
        // database; only the localizing is added.
        var found = (await connection.QueryAsync<Category>(new CommandDefinition(
            $"""SELECT {CategoryColumns} FROM "Categories" ORDER BY "DisplayOrder", "Id" """,
            transaction: unitOfWork.Current, cancellationToken: ct))).ToList();

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
        await EnsureSlugIsFreeAsync(slug, excludingId: null, ct);

        var category = new Category
        {
            Slug = slug,
            DisplayOrder = request.DisplayOrder,
            Name = request.Name,
            Description = request.Description
        };

        const string insert = """
            INSERT INTO "Categories" ("Slug", "DisplayOrder", "Name", "Description")
            VALUES (@Slug, @DisplayOrder, @Name, @Description)
            RETURNING "Id"
            """;

        category.Id = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(insert, category, unitOfWork.Current, cancellationToken: ct));

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

        var assignments = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("Id", id);

        if (request.Slug is not null)
        {
            var slug = request.Slug.Trim();
            await EnsureSlugIsFreeAsync(slug, excludingId: id, ct);

            assignments.Add(""" "Slug" = @Slug """);
            parameters.Add("Slug", slug);
        }

        if (request.DisplayOrder is not null)
        {
            assignments.Add(""" "DisplayOrder" = @DisplayOrder """);
            parameters.Add("DisplayOrder", request.DisplayOrder.Value);
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
                assignments.Add(""" "Name" = @Name """);
                parameters.Add("Name", request.Name);
            }

            if (request.Description is not null)
            {
                assignments.Add(""" "Description" = @Description """);
                parameters.Add("Description", request.Description);
            }
        }

        if (assignments.Count > 0)
        {
            var update = $"""UPDATE "Categories" SET {string.Join(", ", assignments)} WHERE "Id" = @Id""";

            await connection.ExecuteAsync(
                new CommandDefinition(update, parameters, unitOfWork.Current, cancellationToken: ct));
        }

        await multiLingual.For(category).SaveAsync(ct);

        var saved = await FindAsync(id, ct);
        await multiLingual.LocalizeAsync([saved], ct);

        return Ok(ToResponse(saved) with { Message = localizer["Categories.Updated", saved.Slug] });
    }

    /// <summary>
    /// Deletes a category. Its products are kept, with no category — which the EF Core sample gets
    /// from <c>OnDelete(DeleteBehavior.SetNull)</c> in its model. The equivalent here is
    /// <c>ON DELETE SET NULL</c> on the foreign key in <c>Data/DbInitializer.cs</c>, so the database
    /// does it and this method does not have to. Their own translations are untouched, since the
    /// products survive; the category's go with <c>For(category).DeleteAsync()</c>.
    /// </summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType<DeleteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeleteResponse>> Delete(int id, CancellationToken ct)
    {
        var category = await FindAsync(id, ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """DELETE FROM "Categories" WHERE "Id" = @Id""",
            new { Id = id }, unitOfWork.Current, cancellationToken: ct));

        await multiLingual.For(category).DeleteAsync(ct);

        return Ok(new DeleteResponse(category.Id, localizer["Categories.Deleted", category.Slug]));
    }

    private async Task<Category> FindAsync(int id, CancellationToken ct) =>
        await connection.QueryFirstOrDefaultAsync<Category>(new CommandDefinition(
            $"""SELECT {CategoryColumns} FROM "Categories" WHERE "Id" = @Id""",
            new { Id = id }, unitOfWork.Current, cancellationToken: ct))
        ?? throw new NotFoundException("Categories.NotFound", id);

    private async Task EnsureSlugIsFreeAsync(string slug, int? excludingId, CancellationToken ct)
    {
        var taken = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """SELECT EXISTS (SELECT 1 FROM "Categories" WHERE "Slug" = @Slug AND (@Id IS NULL OR "Id" <> @Id))""",
            new { Slug = slug, Id = excludingId }, unitOfWork.Current, cancellationToken: ct));

        if (taken)
        {
            throw new ConflictException("Categories.SlugExists", slug);
        }
    }

    private CategoryResponse ToResponse(Category category) =>
        new(category.Id,
            category.Slug,
            category.DisplayOrder,
            category.Name,
            category.Description,
            multiLingual.CurrentLanguage.Code);
}
