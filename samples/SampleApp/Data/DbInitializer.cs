using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.EntityFrameworkCore;
using SampleApp.Domain;

namespace SampleApp.Data;

/// <summary>
/// Creates the schema and seeds enough data to exercise the mechanism immediately after
/// <c>dotnet run</c>: three languages and a handful of products and categories — deliberately
/// with some translations missing, so the fallback is visible straight away.
///
/// <para>
/// English is the base language here, so English values are set on the entities themselves and
/// stored as ordinary columns; Arabic and French are staged and become translation rows. No
/// language is resolved during seeding, which is why the "create only in the base language" rule
/// does not apply — there is no <c>Accept-Language</c> to be wrong about.
/// </para>
/// </summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, bool applyMigrations, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var db = provider.GetRequiredService<AppDbContext>();
        var languages = provider.GetRequiredService<ILanguageManager>();
        var catalog = provider.GetRequiredService<ILanguageCatalog>();
        var translations = provider.GetRequiredService<ITranslationManager>();

        if (applyMigrations)
            await db.Database.MigrateAsync(ct);
        else
            await db.Database.EnsureCreatedAsync(ct);

        if (await db.Languages.AnyAsync(ct))
            return;

        // Through the manager, so the rules apply: the first language becomes the default.
        await languages.CreateAsync(new NewLanguage { Code = "en", Name = "English" }, ct);
        await languages.CreateAsync(new NewLanguage { Code = "ar", Name = "العربية" }, ct);
        await languages.CreateAsync(new NewLanguage { Code = "fr", Name = "Français" }, ct);

        // Warm the catalog now, before any write transaction is open. The save interceptor
        // validates language codes mid-transaction; having them cached keeps it from opening a
        // second connection mid-save (and, on SQLite, against a locked file).
        await catalog.GetAllAsync(ct);

        // English is the BASE language, so its values are assigned to the properties and land in
        // each entity's own table — Categories.Name, Products.Name — as ordinary columns. Every
        // other language is staged explicitly and becomes a row in Translations.
        //
        // Staging "en" here would be refused: the base language has exactly one home, and these
        // properties are it.
        var electronics = new Category
        {
            Slug = "electronics", DisplayOrder = 1,
            Name = "Electronics", Description = "Devices, gadgets and accessories."
        };
        Stage(translations, electronics, "ar", "إلكترونيات", "أجهزة وملحقات إلكترونية.");
        Stage(translations, electronics, "fr", "Électronique", "Appareils et accessoires.");

        var books = new Category
        {
            Slug = "books", DisplayOrder = 2,
            Name = "Books", Description = "Paper and digital books."
        };
        Stage(translations, books, "ar", "كتب", "كتب ورقية ورقمية.");
        // Deliberately no French: GET /api/categories with Accept-Language: fr falls back to the
        // English column.

        db.Categories.AddRange(electronics, books);
        await db.SaveChangesAsync(ct);

        var laptop = new Product
        {
            Sku = "LAPTOP-14", Price = 1299.00m, CategoryId = electronics.Id,
            Name = "14-inch Laptop", Description = "Lightweight laptop with a 14-inch display."
        };
        Stage(translations, laptop, "ar", "حاسوب محمول 14 بوصة", "حاسوب محمول خفيف بشاشة 14 بوصة.");
        Stage(translations, laptop, "fr", "Ordinateur portable 14 pouces", "Portable léger avec écran de 14 pouces.");

        var mouse = new Product
        {
            Sku = "MOUSE-01", Price = 24.50m, CategoryId = electronics.Id,
            Name = "Wireless Mouse", Description = "Two-button wireless mouse."
        };
        Stage(translations, mouse, "ar", "فأرة لاسلكية", "فأرة لاسلكية بزرين.");
        // No French: proves the fallback on the read path.

        var notebook = new Product
        {
            Sku = "NOTE-A5", Price = 6.90m, CategoryId = books.Id,
            Name = "A5 Notebook", Description = "Dotted A5 notebook, 200 pages."
        };
        // English only: every other language falls back to its columns, and it has no rows at all
        // in Translations.

        db.Products.AddRange(laptop, mouse, notebook);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Queues one non-base language for the next save of <paramref name="entity"/>.</summary>
    private static void Stage(ITranslationManager translations, object entity, string language, string name, string description) =>
        translations.Stage(entity, language, new Dictionary<string, string?>
        {
            ["Name"] = name,
            ["Description"] = description
        });
}
