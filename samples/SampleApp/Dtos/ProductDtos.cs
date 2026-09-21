using System.ComponentModel.DataAnnotations;

namespace SampleApp.Dtos;

/// <summary>
/// The authoring shape: one language per request.
///
/// <para>
/// Base fields carry the entity's own untranslated columns. The translatable fields
/// (<c>Name</c>, <c>Description</c>) carry the values for the AUTHORING language — the one
/// resolved for this request from <c>Accept-Language</c>, defaulting to the system default.
/// To add another language, send a <c>PUT</c> with that language in <c>Accept-Language</c>.
/// </para>
///
/// <para>
/// A <c>null</c> value means "not supplied, leave whatever is stored alone". An empty string
/// means "store an empty value". That distinction is what makes PUT usable as a partial update.
/// </para>
/// </summary>
public sealed record CreateProductRequest
{
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string Sku { get; init; } = string.Empty;

    [Range(0, double.MaxValue)]
    public decimal Price { get; init; }

    public int? CategoryId { get; init; }

    /// <summary>Name in the authoring language.</summary>
    public string? Name { get; init; }

    /// <summary>Description in the authoring language.</summary>
    public string? Description { get; init; }
}

public sealed record UpdateProductRequest
{
    [StringLength(64, MinimumLength = 1)]
    public string? Sku { get; init; }

    [Range(0, double.MaxValue)]
    public decimal? Price { get; init; }

    public int? CategoryId { get; init; }

    /// <summary>Set to null to leave the stored value untouched.</summary>
    public string? Name { get; init; }

    /// <summary>Set to null to leave the stored value untouched.</summary>
    public string? Description { get; init; }
}

public sealed record ProductResponse(
    int Id,
    string Sku,
    decimal Price,
    int? CategoryId,
    string? Name,
    string? Description,
    string Language);

/// <summary>A product row as a list shows it, built by a single translated projection.</summary>
public sealed record ProductSummaryResponse(
    int Id,
    string Sku,
    decimal Price,
    string? Name,
    string? Category,
    string Language);

/// <summary>What a bulk delete removed: the products, and the translation rows they left behind.</summary>
public sealed record BulkDeleteResponse(int DeletedProducts, int PurgedTranslations);

/// <summary>Every stored translation for one product, for verifying the mechanism end to end.</summary>
public sealed record ProductTranslationsResponse(
    int Id,
    string Sku,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Translations);
