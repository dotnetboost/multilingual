using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using MongoDB.Driver;
using SampleApp.MongoDb.Domain;

namespace SampleApp.MongoDb.Data;

/// <summary>
/// The EF Core sample's <c>DbInitializer</c>, over MongoDB. The same three languages and the same
/// products and categories, with the same translations deliberately missing so the fallback is
/// visible on the first request.
///
/// <para>
/// Three things differ, and none of them is a library call. There is no <c>EnsureCreated</c> or
/// <c>Migrate</c> — collections appear on first write, and the provider creates its own indexes when
/// the host starts; the sample's unique indexes are created here, where the EF Core sample declares
/// them in <c>OnModelCreating</c>. Keys are taken from <see cref="IdSequence"/> before the insert.
/// And each entity's translations are saved with <c>For(entity).SaveAsync()</c>, because there is no
/// <c>SaveChanges</c> to collect them.
/// </para>
///
/// <para>
/// English is the base language, so English values are assigned to the properties and stay in each
/// document's own fields; Arabic and French are staged through <c>IMultiLingualManager</c> and
/// become documents in the translations collection. No language is resolved during seeding, which is
/// why the "create only in the base language" rule does not apply — there is no
/// <c>Accept-Language</c> to be wrong about.
/// </para>
/// </summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var products     = provider.GetRequiredService<IMongoCollection<Product>>();
        var categories   = provider.GetRequiredService<IMongoCollection<Category>>();
        var ids          = provider.GetRequiredService<IdSequence>();
        var multiLingual = provider.GetRequiredService<IMultiLingualManager>();

        await CreateIndexesAsync(products, categories, ct);

        if ((await multiLingual.Languages.GetAllAsync(ct)).Count > 0)
            return;

        // Through the manager, so the rules apply: the first language becomes the default.
        await multiLingual.Languages.CreateAsync(new NewLanguage { Code = "en", Name = "English" }, ct);
        await multiLingual.Languages.CreateAsync(new NewLanguage { Code = "ar", Name = "العربية" }, ct);
        await multiLingual.Languages.CreateAsync(new NewLanguage { Code = "fr", Name = "Français" }, ct);

        // Warm the catalog before any writes, as the EF Core sample does.
        await multiLingual.Languages.GetDefaultAsync(ct);

        var electronics = new Category
        {
            Id = await ids.NextAsync("categories", ct),
            Slug = "electronics", DisplayOrder = 1,
            Name = "Electronics", Description = "Devices, gadgets and accessories."
        };
        Translate(multiLingual, electronics, "ar", "إلكترونيات", "أجهزة وملحقات إلكترونية.");
        Translate(multiLingual, electronics, "fr", "Électronique", "Appareils et accessoires.");

        var books = new Category
        {
            Id = await ids.NextAsync("categories", ct),
            Slug = "books", DisplayOrder = 2,
            Name = "Books", Description = "Paper and digital books."
        };
        Translate(multiLingual, books, "ar", "كتب", "كتب ورقية ورقمية.");
        // Deliberately no French: GET /api/categories with Accept-Language: fr falls back to the
        // document's English field.

        await SaveAllAsync(categories, multiLingual, [electronics, books], ct);

        var laptop = new Product
        {
            Id = await ids.NextAsync("products", ct),
            Sku = "LAPTOP-14", Price = 1299.00m, CategoryId = electronics.Id,
            Name = "14-inch Laptop", Description = "Lightweight laptop with a 14-inch display."
        };
        Translate(multiLingual, laptop, "ar", "حاسوب محمول 14 بوصة", "حاسوب محمول خفيف بشاشة 14 بوصة.");
        Translate(multiLingual, laptop, "fr", "Ordinateur portable 14 pouces", "Portable léger avec écran de 14 pouces.");

        var mouse = new Product
        {
            Id = await ids.NextAsync("products", ct),
            Sku = "MOUSE-01", Price = 24.50m, CategoryId = electronics.Id,
            Name = "Wireless Mouse", Description = "Two-button wireless mouse."
        };
        Translate(multiLingual, mouse, "ar", "فأرة لاسلكية", "فأرة لاسلكية بزرين.");
        // No French: proves the fallback on the read path.

        var notebook = new Product
        {
            Id = await ids.NextAsync("products", ct),
            Sku = "NOTE-A5", Price = 6.90m, CategoryId = books.Id,
            Name = "A5 Notebook", Description = "Dotted A5 notebook, 200 pages."
        };
        // English only: no translation documents at all, and every other language falls back.

        await SaveAllAsync(products, multiLingual, [laptop, mouse, notebook], ct);
    }

    /// <summary>
    /// The documents, then their translations. In base-language mode the English text rides along in
    /// each document's own fields, so <c>SaveAsync</c> writes only what <see cref="Translate"/>
    /// staged — and it has to run after the insert, because translations hang off a key.
    /// </summary>
    private static async Task SaveAllAsync<T>(
        IMongoCollection<T> collection, IMultiLingualManager multiLingual, T[] entities, CancellationToken ct)
        where T : class
    {
        await collection.InsertManyAsync(entities, cancellationToken: ct);

        foreach (var entity in entities)
            await multiLingual.For(entity).SaveAsync(ct);
    }

    /// <summary>
    /// The unique indexes the EF Core sample declares with <c>HasIndex(...).IsUnique()</c>. MongoDB
    /// has no model to declare them in, so they are created here — idempotently, like the
    /// provider's own.
    /// </summary>
    private static async Task CreateIndexesAsync(
        IMongoCollection<Product> products, IMongoCollection<Category> categories, CancellationToken ct)
    {
        await products.Indexes.CreateOneAsync(
            new CreateIndexModel<Product>(
                Builders<Product>.IndexKeys.Ascending(p => p.Sku),
                new CreateIndexOptions { Unique = true, Name = "ux_product_sku" }),
            cancellationToken: ct);

        await categories.Indexes.CreateOneAsync(
            new CreateIndexModel<Category>(
                Builders<Category>.IndexKeys.Ascending(c => c.Slug),
                new CreateIndexOptions { Unique = true, Name = "ux_category_slug" }),
            cancellationToken: ct);
    }

    /// <summary>Queues one non-base language for the next save of <paramref name="entity"/>.</summary>
    private static void Translate<T>(IMultiLingualManager multiLingual, T entity, string language, string name, string description)
        where T : class =>
        multiLingual.For(entity).Set(language, new Dictionary<string, string?>
        {
            ["Name"] = name,
            ["Description"] = description
        });
}
