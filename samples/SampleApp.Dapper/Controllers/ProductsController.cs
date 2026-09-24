using Dapper;
using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SampleApp.Dapper.Data;
using SampleApp.Dapper.Domain;
using SampleApp.Dtos;
using SampleApp.Infrastructure;
using System.Data;

namespace SampleApp.Dapper.Controllers;

/// <summary>
/// The EF Core sample's <c>ProductsController</c>, endpoint for endpoint, over Dapper. Same routes,
/// same request and response types — literally the same DTO files — so <c>clients/dashboard</c> can
/// be pointed at any of the three APIs.
/// <para>
/// What differs is that there is no change tracker to hook, so three things the EF Core interceptors
/// do quietly are written out here:
/// </para>
/// <list type="number">
///   <item><b>Localize after a query.</b> A SELECT fills an object's own columns; the translatable
///   ones arrive null until <c>LocalizeAsync</c> fills them.</item>
///   <item><b>Save translations after the row</b>, so its key exists —
///   <c>For(entity).SaveAsync()</c> where the EF sample relies on <c>SaveChanges</c>.</item>
///   <item><b>Apply the base-language rules yourself.</b> See <see cref="Create"/> and
///   <see cref="Update"/>; they are the two easiest things to get wrong.</item>
/// </list>
/// <para>
/// One thing is simply not available: filtering and sorting on TRANSLATED text inside the database.
/// See <see cref="GetAll"/>.
/// </para>
/// <para>
/// And one thing IS, which the MongoDB sample cannot show: every method below runs inside a real
/// transaction, so a row and its translations commit or roll back together. Nothing here says so —
/// <c>Data/UnitOfWork.cs</c> puts an <c>IDbTransaction</c> in the request scope and the library's
/// stores enlist in it. The only trace of it in this file is that each Dapper call carries
/// <c>unitOfWork.Current</c>, which ADO.NET requires once a connection has an open transaction.
/// docs/dapper.md covers all four.
/// </para>
/// </summary>
[ApiController]
[Route("api/products")]
[Produces("application/json")]
public sealed class ProductsController(
    IDbConnection connection,
    UnitOfWork unitOfWork,
    IMultiLingualManager multiLingual,
    ITranslationWriter writer,
    IStringLocalizer<SharedResource> localizer)
    : ControllerBase
{
    private const string ProductColumns = """ "Id", "Sku", "Price", "CategoryId", "Name", "Description" """;

    /// <summary>
    /// Lists products, each resolved to the requested language
    /// (<c>Accept-Language</c>, then the system default).
    /// <para>
    /// <c>categoryId</c> is an ordinary column filter and stays in the database. <c>search</c> and
    /// <c>sort</c> are on the TRANSLATED name, and that is the one thing the EF Core provider does
    /// which no hand-written query can: it rewrites the LINQ query so the comparison happens in SQL.
    /// Here they run after <c>LocalizeAsync</c> instead. Fine for a page of results; a large table
    /// wants a JOIN against <c>Translations</c> written by hand, which is exactly the query the EF
    /// Core provider generates for you.
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
        var sql = $"""
            SELECT {ProductColumns} FROM "Products"
            WHERE (@CategoryId IS NULL OR "CategoryId" = @CategoryId)
            ORDER BY "Id"
            """;

        var found = (await connection.QueryAsync<Product>(
            new CommandDefinition(sql, new { CategoryId = categoryId }, unitOfWork.Current, cancellationToken: ct)))
            .ToList();

        // Name and Description hold the English column text at this point; this line resolves them
        // to the requested language, falling back where there is no translation.
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
    /// The EF Core sample reads both in one statement, through the <c>Category</c> navigation.
    /// Dapper has no navigation, so each table is read and localized on its own and joined here — a
    /// SQL JOIN would bring back the category's English column, not its translation.
    /// <c>LocalizeAsync</c> batches per entity type, so this is two translation queries in total
    /// however many products come back, not one per row.
    /// </para>
    /// </summary>
    [HttpGet("summaries")]
    [ProducesResponseType<IReadOnlyList<ProductSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProductSummaryResponse>>> GetSummaries(CancellationToken ct)
    {
        var languageCode = multiLingual.CurrentLanguage.Code;

        var found = (await connection.QueryAsync<Product>(new CommandDefinition(
            $"""SELECT {ProductColumns} FROM "Products" ORDER BY "Id" """,
            transaction: unitOfWork.Current, cancellationToken: ct))).ToList();

        var all = (await connection.QueryAsync<Category>(new CommandDefinition(
            """SELECT "Id", "Slug", "DisplayOrder", "Name", "Description" FROM "Categories" """,
            transaction: unitOfWork.Current, cancellationToken: ct))).ToList();

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
    /// table's own columns and the request must carry <c>Accept-Language: en</c>. Add other
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
        // intercepts a hand-written INSERT, so the check is here — without it, an Arabic request
        // would put Arabic text in the column that is supposed to hold English.
        //
        // The same exception EF Core throws, so MultiLingualExceptionHandler still answers 409.
        RequireBaseLanguage();

        await EnsureCategoryExistsAsync(request.CategoryId, ct);

        var sku = request.Sku.Trim();
        await EnsureSkuIsFreeAsync(sku, excludingId: null, ct);

        var product = new Product
        {
            Sku = sku,
            Price = request.Price,
            CategoryId = request.CategoryId,
            Name = request.Name,
            Description = request.Description
        };

        // The row first, so its key exists before anything refers to it — RETURNING hands it back
        // where the EF Core sample reads it off the tracked entity. In base-language mode the
        // English text is an ordinary column, so the INSERT stores it and SaveAsync finds nothing to
        // file as a translation. Without a base language there would be no such column and SaveAsync
        // would store the English too. Either way the call belongs here: it is also what writes
        // anything an earlier Set(...) queued.
        //
        // Both statements are in the request's transaction, so a failure between them leaves
        // neither behind.
        const string insert = """
            INSERT INTO "Products" ("Sku", "Price", "CategoryId", "Name", "Description")
            VALUES (@Sku, @Price, @CategoryId, @Name, @Description)
            RETURNING "Id"
            """;

        product.Id = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(insert, product, unitOfWork.Current, cancellationToken: ct));

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

        var assignments = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("Id", id);

        if (request.Sku is not null)
        {
            var sku = request.Sku.Trim();
            await EnsureSkuIsFreeAsync(sku, excludingId: id, ct);

            assignments.Add(""" "Sku" = @Sku """);
            parameters.Add("Sku", sku);
        }

        if (request.Price is not null)
        {
            assignments.Add(""" "Price" = @Price """);
            parameters.Add("Price", request.Price.Value);
        }

        if (request.CategoryId is not null)
        {
            await EnsureCategoryExistsAsync(request.CategoryId, ct);

            assignments.Add(""" "CategoryId" = @CategoryId """);
            parameters.Add("CategoryId", request.CategoryId);
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

        // (2) The translatable values go into the ROW only when the request's language is the base
        // one, because that column is where the base language lives. In any other language they
        // belong in Translations — writing them here would overwrite the English — so the UPDATE
        // deliberately leaves them out and SaveAsync files them instead. Without a base language
        // there are no such columns and this branch never runs.
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
            // The assignments are literals from this method, never anything a caller sent; every
            // value travels as a parameter.
            var update = $"""UPDATE "Products" SET {string.Join(", ", assignments)} WHERE "Id" = @Id""";

            await connection.ExecuteAsync(
                new CommandDefinition(update, parameters, unitOfWork.Current, cancellationToken: ct));
        }

        await multiLingual.For(product).SaveAsync(ct);

        // Re-read, so the response shows what is actually stored for the requested language.
        var saved = await FindAsync(id, ct);
        await multiLingual.LocalizeAsync([saved], ct);

        return Ok(ToResponse(saved) with { Message = localizer["Products.Updated", saved.Sku] });
    }

    /// <summary>
    /// Removes one language's stored translation of a product — every translatable property in that
    /// language. Reads in it then fall back, exactly as for a language never written.
    /// <para>
    /// A <c>PUT</c> cannot do this: a <c>null</c> field there means "not supplied". And English, the
    /// base language, is not a translation at all — it is the row's own column — so it is refused
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
    /// works by joining <c>Translations</c> against each entity's own table through the model, and
    /// Dapper has no model to join through. So collect the keys BEFORE deleting the rows and remove
    /// their translations explicitly — <c>ITranslationWriter.DeleteEntitiesAsync</c> is one call for
    /// any number of entities. Once the rows are gone, nothing can tell you which keys they had.
    /// </para>
    /// <para>
    /// Both halves are in the request's transaction, so unlike the MongoDB sample there is no window
    /// in which the products are gone and their translations are not.
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
        var doomed = (await connection.QueryAsync<int>(new CommandDefinition(
            """SELECT "Id" FROM "Products" WHERE "CategoryId" = @CategoryId""",
            new { CategoryId = categoryId }, unitOfWork.Current, cancellationToken: ct))).ToList();

        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            """DELETE FROM "Products" WHERE "CategoryId" = @CategoryId""",
            new { CategoryId = categoryId }, unitOfWork.Current, cancellationToken: ct));

        var purged = await writer.DeleteEntitiesAsync(
            MultiLingualMetadata.EntityNameFor(typeof(Product)),
            doomed.Select(key => EntityKey.ToKeyString(key)).ToArray(),
            ct);

        return Ok(new BulkDeleteResponse(deleted, purged, localizer["Products.BulkDeleted", deleted, purged]));
    }

    /// <summary>
    /// Deletes a product and its translations. The EF Core save interceptor does the second half in
    /// the same transaction; here it is one more call — in the same transaction all the same,
    /// because the request has one.
    /// </summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType<DeleteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeleteResponse>> Delete(int id, CancellationToken ct)
    {
        var product = await FindAsync(id, ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """DELETE FROM "Products" WHERE "Id" = @Id""",
            new { Id = id }, unitOfWork.Current, cancellationToken: ct));

        await multiLingual.For(product).DeleteAsync(ct);

        return Ok(new DeleteResponse(product.Id, localizer["Products.Deleted", product.Sku]));
    }

    private async Task<Product> FindAsync(int id, CancellationToken ct) =>
        await connection.QueryFirstOrDefaultAsync<Product>(new CommandDefinition(
            $"""SELECT {ProductColumns} FROM "Products" WHERE "Id" = @Id""",
            new { Id = id }, unitOfWork.Current, cancellationToken: ct))
        ?? throw new NotFoundException("Products.NotFound", id);

    private async Task EnsureCategoryExistsAsync(int? categoryId, CancellationToken ct)
    {
        if (categoryId is null)
        {
            return;
        }

        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """SELECT EXISTS (SELECT 1 FROM "Categories" WHERE "Id" = @Id)""",
            new { Id = categoryId }, unitOfWork.Current, cancellationToken: ct));

        if (!exists)
        {
            throw new ValidationException("Categories.NotFound", categoryId);
        }
    }

    private async Task EnsureSkuIsFreeAsync(string sku, int? excludingId, CancellationToken ct)
    {
        var taken = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """SELECT EXISTS (SELECT 1 FROM "Products" WHERE "Sku" = @Sku AND (@Id IS NULL OR "Id" <> @Id))""",
            new { Sku = sku, Id = excludingId }, unitOfWork.Current, cancellationToken: ct));

        if (taken)
        {
            throw new ConflictException("Products.SkuExists", sku);
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
