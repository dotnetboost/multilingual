using Dapper;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using SampleApp.Dapper.Domain;
using System.Data;

namespace SampleApp.Dapper.Data;

/// <summary>
/// The EF Core sample's <c>DbInitializer</c>, over Dapper. The same three languages and the same
/// products and categories, with the same translations deliberately missing so the fallback is
/// visible on the first request.
///
/// <para>
/// Three things differ, and none of them is a library call. The sample's OWN tables are created
/// here with DDL, where the EF Core sample calls <c>EnsureCreated</c>/<c>Migrate</c> — but the
/// library's two tables are NOT, because <c>UseDapper(..., migrateSchema: true)</c> in
/// <c>Program.cs</c> has already created them by the time this runs. Keys come back from
/// <c>RETURNING "Id"</c> rather than from the change tracker. And each entity's translations are
/// saved with <c>For(entity).SaveAsync()</c>, because there is no <c>SaveChanges</c> to collect
/// them.
/// </para>
///
/// <para>
/// English is the base language, so English values are assigned to the properties and land in each
/// table's own columns; Arabic and French are staged through <c>IMultiLingualManager</c> and become
/// rows in <c>Translations</c>. No language is resolved during seeding, which is why the "create
/// only in the base language" rule does not apply — there is no <c>Accept-Language</c> to be wrong
/// about.
/// </para>
/// </summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        var connection   = provider.GetRequiredService<IDbConnection>();
        var unitOfWork   = provider.GetRequiredService<UnitOfWork>();
        var multiLingual = provider.GetRequiredService<IMultiLingualManager>();

        await CreateSampleTablesAsync(connection, ct);

        if ((await multiLingual.Languages.GetAllAsync(ct)).Count > 0)
        {
            // Nothing staged, but the schema check above may have opened a transaction.
            await unitOfWork.CommitAsync(ct);
            return;
        }

        // Through the manager, so the rules apply: the first language becomes the default.
        await multiLingual.Languages.CreateAsync(new NewLanguage { Code = "en", Name = "English" }, ct);
        await multiLingual.Languages.CreateAsync(new NewLanguage { Code = "ar", Name = "العربية" }, ct);
        await multiLingual.Languages.CreateAsync(new NewLanguage { Code = "fr", Name = "Français" }, ct);

        // Commit the languages before anything reads them back, and this one is worth understanding.
        // ILanguageManager.CreateAsync goes through the SCOPED store, so it sees rows still inside
        // this transaction — which is why "the first language becomes the default" came out right.
        // The language CATALOG does not: it refreshes in a scope of its own, on its own connection,
        // so uncommitted rows are invisible to it and GetDefaultAsync below would report that no
        // language exists. Anything holding a transaction open across language administration has
        // to commit before the catalog is consulted.
        await unitOfWork.CommitAsync(ct);

        // Warm the catalog before any writes, as the EF Core sample does.
        await multiLingual.Languages.GetDefaultAsync(ct);

        var electronics = new Category
        {
            Slug = "electronics", DisplayOrder = 1,
            Name = "Electronics", Description = "Devices, gadgets and accessories."
        };
        Translate(multiLingual, electronics, "ar", "إلكترونيات", "أجهزة وملحقات إلكترونية.");
        Translate(multiLingual, electronics, "fr", "Électronique", "Appareils et accessoires.");

        var books = new Category
        {
            Slug = "books", DisplayOrder = 2,
            Name = "Books", Description = "Paper and digital books."
        };
        Translate(multiLingual, books, "ar", "كتب", "كتب ورقية ورقمية.");
        // Deliberately no French: GET /api/categories with Accept-Language: fr falls back to the
        // English column.

        foreach (var category in new[] { electronics, books })
        {
            await InsertCategoryAsync(connection, unitOfWork, category, ct);
            await multiLingual.For(category).SaveAsync(ct);
        }

        var laptop = new Product
        {
            Sku = "LAPTOP-14", Price = 1299.00m, CategoryId = electronics.Id,
            Name = "14-inch Laptop", Description = "Lightweight laptop with a 14-inch display."
        };
        Translate(multiLingual, laptop, "ar", "حاسوب محمول 14 بوصة", "حاسوب محمول خفيف بشاشة 14 بوصة.");
        Translate(multiLingual, laptop, "fr", "Ordinateur portable 14 pouces", "Portable léger avec écran de 14 pouces.");

        var mouse = new Product
        {
            Sku = "MOUSE-01", Price = 24.50m, CategoryId = electronics.Id,
            Name = "Wireless Mouse", Description = "Two-button wireless mouse."
        };
        Translate(multiLingual, mouse, "ar", "فأرة لاسلكية", "فأرة لاسلكية بزرين.");
        // No French: proves the fallback on the read path.

        var notebook = new Product
        {
            Sku = "NOTE-A5", Price = 6.90m, CategoryId = books.Id,
            Name = "A5 Notebook", Description = "Dotted A5 notebook, 200 pages."
        };
        // English only: no translation rows at all, and every other language falls back.

        foreach (var product in new[] { laptop, mouse, notebook })
        {
            await InsertProductAsync(connection, unitOfWork, product, ct);
            await multiLingual.For(product).SaveAsync(ct);
        }

        // Seeding runs outside a request, so UnitOfWorkFilter is not there to commit. Without this
        // the scope would dispose the transaction and roll the whole seed back — silently, leaving
        // an empty database and a very confusing first request.
        await unitOfWork.CommitAsync(ct);
    }

    /// <summary>
    /// The sample's OWN tables — the equivalent of the EF Core sample's migrations, including the
    /// unique indexes it declares with <c>HasIndex(...).IsUnique()</c> and the "keep the products,
    /// clear their category" rule it gets from <c>OnDelete(DeleteBehavior.SetNull)</c>.
    /// <para>
    /// <c>Translations</c> and <c>Languages</c> are absent on purpose: they are the library's, and
    /// <c>migrateSchema: true</c> has already created them. That split is the whole point of the
    /// flag — your schema stays yours.
    /// </para>
    /// <para>
    /// Note which columns are here. <c>Name</c> and <c>Description</c> have columns because this
    /// sample sets <c>BaseLanguage = "en"</c>, and that is where the English text lives. Drop the
    /// base language and they come out of this DDL, out of the INSERTs and out of the SELECTs —
    /// with Dapper there is nothing else to change, because "unmapped" is just a column your SQL
    /// does not name.
    /// </para>
    /// </summary>
    private static async Task CreateSampleTablesAsync(IDbConnection connection, CancellationToken ct)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS "Categories" (
                "Id"           integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "Slug"         character varying(64)  NOT NULL,
                "DisplayOrder" integer                NOT NULL,
                "Name"         character varying(256) NULL,
                "Description"  text                   NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Categories_Slug" ON "Categories" ("Slug");

            CREATE TABLE IF NOT EXISTS "Products" (
                "Id"          integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "Sku"         character varying(64)  NOT NULL,
                "Price"       numeric(18,2)          NOT NULL,
                "CategoryId"  integer                NULL REFERENCES "Categories" ("Id") ON DELETE SET NULL,
                "Name"        character varying(256) NULL,
                "Description" text                   NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Products_Sku" ON "Products" ("Sku");
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }

    /// <summary>
    /// Inserts a category and takes its generated key back. The EF Core sample gets this from the
    /// change tracker; with Dapper it is <c>RETURNING</c>, and it has to happen before
    /// <c>SaveAsync</c> because translations hang off a key.
    /// </summary>
    private static async Task InsertCategoryAsync(
        IDbConnection connection, UnitOfWork unitOfWork, Category category, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO "Categories" ("Slug", "DisplayOrder", "Name", "Description")
            VALUES (@Slug, @DisplayOrder, @Name, @Description)
            RETURNING "Id"
            """;

        category.Id = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, category, unitOfWork.Current, cancellationToken: ct));
    }

    private static async Task InsertProductAsync(
        IDbConnection connection, UnitOfWork unitOfWork, Product product, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO "Products" ("Sku", "Price", "CategoryId", "Name", "Description")
            VALUES (@Sku, @Price, @CategoryId, @Name, @Description)
            RETURNING "Id"
            """;

        product.Id = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, product, unitOfWork.Current, cancellationToken: ct));
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
