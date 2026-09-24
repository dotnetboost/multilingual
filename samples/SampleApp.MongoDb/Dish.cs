using DotNetBoost.MultiLingual.Core.Attributes;
using MongoDB.Bson;

namespace SampleApp.MongoDb;

/// <summary>
/// One entity, so the MongoDB flow stays in view. <see cref="Name"/> and
/// <see cref="Description"/> are translatable exactly as in the EF Core sample — the attribute is
/// provider-neutral, and nothing here mentions MongoDB.
/// <para>
/// The key is an <see cref="ObjectId"/>, which the driver assigns during the insert. Until then it
/// is <c>ObjectId.Empty</c>, and the provider reads that as "no key yet": saving translations for a
/// document that has not been inserted is refused rather than stored under an empty id.
/// </para>
/// </summary>
public sealed class Dish
{
    public ObjectId Id { get; set; }

    /// <summary>Not translatable — a price is a price.</summary>
    public decimal Price { get; set; }

    /// <summary>Translatable.</summary>
    [MultiLingual]
    public string? Name { get; set; }

    /// <summary>Translatable.</summary>
    [MultiLingual]
    public string? Description { get; set; }
}

/// <summary>What the API returns: the dish resolved to one language.</summary>
public sealed record DishResponse(string Id, decimal Price, string? Name, string? Description, string Language);

/// <summary>Every stored language for one dish.</summary>
public sealed record DishTranslationsResponse(
    string Id,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Translations);

/// <summary>A create. Name and Description are in the request's language.</summary>
public sealed record CreateDishRequest(decimal Price, string? Name, string? Description);

/// <summary>An update. A null field means "leave it alone", as in the EF Core sample.</summary>
public sealed record UpdateDishRequest(decimal? Price, string? Name, string? Description);
