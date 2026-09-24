using Dapper;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.Dapper;
using DotNetBoost.MultiLingual.EntityFrameworkCore;
using DotNetBoost.MultiLingual.ProviderTests.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Npgsql;
using System.Data;
using System.Data.Common;

namespace DotNetBoost.MultiLingual.IntegrationTests;

/// <summary>
/// Hands out open connections to fresh databases on a real server, each with the provider's own
/// schema already created, and disposes them afterwards.
/// </summary>
internal sealed class ConnectionPool(Func<Task<string>> createDatabase, Func<string, DbConnection> connect)
    : IAsyncDisposable
{
    private readonly List<DbConnection> _connections = [];

    public async Task<IDbConnection> NewAsync() => (await NewWithConnectionStringAsync()).Connection;

    /// <summary>
    /// As <see cref="NewAsync"/>, also handing back the connection string it was opened with —
    /// Npgsql strips the password from <c>ConnectionString</c> once the connection is open, so a
    /// caller that needs to open a second connection to the same database cannot read it back off.
    /// </summary>
    public async Task<(IDbConnection Connection, string ConnectionString)> NewWithConnectionStringAsync()
    {
        var connectionString = await createDatabase();
        var connection = connect(connectionString);
        await connection.OpenAsync();
        _connections.Add(connection);

        await DapperSchema.EnsureCreatedAsync(connection);
        return (connection, connectionString);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
            await connection.DisposeAsync();
    }
}

/// <summary>
/// The full <see cref="TranslationStoreContractTests"/> suite against PostgreSQL: the composite
/// primary key, <c>INSERT … ON CONFLICT</c> upserts, the foreign key to Languages, and unbounded
/// text values on a real engine.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DapperPostgresTranslationStoreTests(PostgreSqlFixture fixture) : TranslationStoreContractTests, IAsyncDisposable
{
    private readonly ConnectionPool _pool = new(fixture.CreateDatabaseAsync, cs => new NpgsqlConnection(cs));

    protected override async Task<(ITranslationStore Translations, ILanguageStore Languages)> CreateStoresAsync()
    {
        var connection = await _pool.NewAsync();
        return (new DapperTranslationStore(connection), new DapperLanguageStore(connection));
    }

    /// <summary>
    /// The EF Core provider creates quoted, PascalCase identifiers on PostgreSQL. An unquoted name
    /// here would be folded to lower case and look for a table that provider never created, so the
    /// two would silently stop sharing a database.
    /// </summary>
    [Fact]
    public async Task TablesArePascalCase_AsTheEfCoreProviderCreatesThem()
    {
        var connection = await _pool.NewAsync();

        var tables = (await connection.QueryAsync<string>(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename")).ToList();

        Assert.Equal(["Languages", "Translations"], tables);
    }

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DapperPostgresLanguageStoreTests(PostgreSqlFixture fixture) : LanguageStoreContractTests, IAsyncDisposable
{
    private readonly ConnectionPool _pool = new(fixture.CreateDatabaseAsync, cs => new NpgsqlConnection(cs));

    protected override async Task<ILanguageStore> CreateStoreAsync() => new DapperLanguageStore(await _pool.NewAsync());

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}

/// <summary>
/// The same suite against SQL Server, where upserts go through <c>MERGE</c> and the composite
/// primary key has to fit the 900-byte clustered index limit (128 + 64 + 128 + 16 nvarchar
/// characters = 672 bytes).
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DapperSqlServerTranslationStoreTests(SqlServerFixture fixture) : TranslationStoreContractTests, IAsyncDisposable
{
    private readonly ConnectionPool _pool = new(fixture.CreateDatabaseAsync, cs => new SqlConnection(cs));

    protected override async Task<(ITranslationStore Translations, ILanguageStore Languages)> CreateStoresAsync()
    {
        var connection = await _pool.NewAsync();
        return (new DapperTranslationStore(connection), new DapperLanguageStore(connection));
    }

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}

[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DapperSqlServerLanguageStoreTests(SqlServerFixture fixture) : LanguageStoreContractTests, IAsyncDisposable
{
    private readonly ConnectionPool _pool = new(fixture.CreateDatabaseAsync, cs => new SqlConnection(cs));

    protected override async Task<ILanguageStore> CreateStoreAsync() => new DapperLanguageStore(await _pool.NewAsync());

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}

/// <summary>
/// The claim the Dapper schema makes about itself: a database it creates is one the EF Core
/// provider will read and write, and vice versa.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DapperEfCoreSchemaCompatibilityTests(PostgreSqlFixture fixture) : IAsyncDisposable
{
    private readonly ConnectionPool _pool = new(fixture.CreateDatabaseAsync, cs => new NpgsqlConnection(cs));

    [Fact]
    public async Task EfCoreReadsWhatTheDapperSchemaAndStoresWrote()
    {
        var (connection, connectionString) = await _pool.NewWithConnectionStringAsync();

        await new DapperLanguageStore(connection).AddAsync(
            new Language { Code = "ar", Name = "العربية", Direction = TextDirection.Rtl, IsDefault = true });
        await new DapperTranslationStore(connection).UpsertManyAsync(
            [new Translation { EntityName = "Product", EntityId = "1", PropertyName = "Name", LanguageCode = "ar", Value = "حاسوب" }]);

        // The EF Core stores over the very same database, mapped by their own configuration.
        await using var context = new StoreDbContext(
            new DbContextOptionsBuilder<StoreDbContext>().UseNpgsql(connectionString).Options);

        var language = Assert.Single(await new EfCoreLanguageStore(context).GetAllAsync());
        Assert.Equal(("ar", TextDirection.Rtl, true), (language.Code, language.Direction, language.IsDefault));

        var row = Assert.Single(await new EfCoreTranslationStore(context).GetAllAsync("Product", "1"));
        Assert.Equal("حاسوب", row.Value);
    }

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}
