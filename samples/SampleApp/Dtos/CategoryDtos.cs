using System.ComponentModel.DataAnnotations;

namespace SampleApp.Dtos;

/// <summary>
/// Category exercises the CLASS-LEVEL attribute: <c>Name</c> and <c>Description</c> are
/// translated automatically, while <c>Slug</c> (marked <c>[NotMultiLingual]</c>) and
/// <c>DisplayOrder</c> stay ordinary columns.
/// </summary>
public sealed record CreateCategoryRequest
{
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string Slug { get; init; } = string.Empty;

    public int DisplayOrder { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }
}

public sealed record UpdateCategoryRequest
{
    [StringLength(128, MinimumLength = 1)]
    public string? Slug { get; init; }

    public int? DisplayOrder { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }
}

/// <param name="Message">
/// A human-readable confirmation in the request's language — set only by create and update, and
/// left out of the JSON everywhere else.
/// </param>
public sealed record CategoryResponse(
    int Id,
    string Slug,
    int DisplayOrder,
    string? Name,
    string? Description,
    string Language,
    string? Message = null);

public sealed record CategoryTranslationsResponse(
    int Id,
    string Slug,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Translations);
