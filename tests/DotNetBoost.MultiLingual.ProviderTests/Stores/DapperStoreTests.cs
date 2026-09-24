using Dapper;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using DotNetBoost.MultiLingual.Dapper;
using Microsoft.Data.Sqlite;
using System.Data;

namespace DotNetBoost.MultiLingual.ProviderTests.Stores;

/// <summary>
/// Fresh in-memory SQLite databases, schema created by the provider's own
/// <see cref="DapperSchema"/>. Each lives as long as its open connection, so the DDL under test
/// is also what every one of these tests runs against.
/// </summary>
public abstract class DapperSqliteFixture : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    protected async Task<IDbConnection> NewConnectionAsync()
    {
        // Foreign Keys=True in the connection string, not a PRAGMA afterwards: SQLite defaults the
        // enforcement off, and the foreign key from Translations to Languages is part of the shape
        // the contract suite relies on.
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        _owned.Add(connection);

        await DapperSchema.EnsureCreatedAsync(connection);
        return connection;
    }

    public void Dispose()
    {
        foreach (var item in _owned)
            item.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class DapperTranslationStoreTests : TranslationStoreContractTests, IDisposable
{
    private readonly Fixture _fixture = new();

    protected override async Task<(ITranslationStore Translations, ILanguageStore Languages)> CreateStoresAsync()
    {
        var connection = await _fixture.NewAsync();
        return (new DapperTranslationStore(connection), new DapperLanguageStore(connection));
    }

    [Fact]
    public async Task ForeignKey_RejectsAnUnknownLanguage()
    {
        var connection = await _fixture.NewAsync();

        // ILanguageManager refuses unknown codes before they get here; the database must too.
        await Assert.ThrowsAsync<SqliteException>(() => new DapperTranslationStore(connection).UpsertManyAsync(
            [Row("1", "Name", "de", "Laptop")]));
    }

    [Fact]
    public async Task ForeignKey_BlocksDeletingALanguageWithContent()
    {
        var connection = await _fixture.NewAsync();
        await new DapperLanguageStore(connection).AddAsync(new Language { Code = "en", Name = "English", IsDefault = true });
        await new DapperTranslationStore(connection).UpsertManyAsync([Row("1", "Name", "en", "Laptop")]);

        await Assert.ThrowsAsync<SqliteException>(() => new DapperLanguageStore(connection).DeleteAsync("en"));
    }

    /// <summary>
    /// The write side of the unit-of-work seam: a store bound to the caller's transaction must not
    /// leave its rows behind when that transaction rolls back.
    /// </summary>
    [Fact]
    public async Task WithTransaction_RollsTranslationsBackWithTheCallersTransaction()
    {
        var connection = await _fixture.NewAsync();
        var languages = new DapperLanguageStore(connection);
        await languages.AddAsync(new Language { Code = "en", Name = "English", IsDefault = true });

        var store = new DapperTranslationStore(connection);
        using (var transaction = connection.BeginTransaction())
        {
            await store.WithTransaction(transaction).UpsertManyAsync([Row("1", "Name", "en", "Laptop")]);

            // Visible inside the unit of work…
            Assert.Single(await store.WithTransaction(transaction).GetAllAsync("Product", "1"));
            transaction.Rollback();
        }

        // …and gone once it rolls back.
        Assert.Empty(await store.GetAllAsync("Product", "1"));
    }

    [Fact]
    public async Task WithTransaction_CommitsTranslationsWithTheCallersTransaction()
    {
        var connection = await _fixture.NewAsync();
        await new DapperLanguageStore(connection).AddAsync(new Language { Code = "en", Name = "English", IsDefault = true });

        var store = new DapperTranslationStore(connection);
        using (var transaction = connection.BeginTransaction())
        {
            await store.WithTransaction(transaction).UpsertManyAsync([Row("1", "Name", "en", "Laptop")]);
            transaction.Commit();
        }

        Assert.Equal("Laptop", Assert.Single(await store.GetAllAsync("Product", "1")).Value);
    }

    public void Dispose() => _fixture.Dispose();

    private sealed class Fixture : DapperSqliteFixture
    {
        public Task<IDbConnection> NewAsync() => NewConnectionAsync();
    }
}

public sealed class DapperLanguageStoreTests : LanguageStoreContractTests, IDisposable
{
    private readonly Fixture _fixture = new();

    protected override async Task<ILanguageStore> CreateStoreAsync() => new DapperLanguageStore(await _fixture.NewAsync());

    /// <summary>
    /// The column stores the name, as the EF Core provider's <c>HasConversion&lt;string&gt;()</c>
    /// does — so the two providers read each other's rows, and reordering the enum cannot silently
    /// reinterpret stored data.
    /// </summary>
    [Fact]
    public async Task DirectionIsStoredAsItsName()
    {
        var connection = await _fixture.NewAsync();
        await new DapperLanguageStore(connection).AddAsync(
            new Language { Code = "ar", Name = "العربية", Direction = TextDirection.Rtl, IsDefault = true });

        Assert.Equal("Rtl", await connection.ExecuteScalarAsync<string>(
            """SELECT "Direction" FROM "Languages" WHERE "Code" = 'ar'"""));
    }

    public void Dispose() => _fixture.Dispose();

    private sealed class Fixture : DapperSqliteFixture
    {
        public Task<IDbConnection> NewAsync() => NewConnectionAsync();
    }
}

/// <summary>The DDL <c>migrateSchema: true</c> runs, checked where it is cheapest to run it.</summary>
public sealed class DapperSchemaTests : IDisposable
{
    private readonly List<SqliteConnection> _owned = [];

    private async Task<SqliteConnection> NewConnectionAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        _owned.Add(connection);
        return connection;
    }

    [Fact]
    public async Task EnsureCreated_IsIdempotent()
    {
        var connection = await NewConnectionAsync();

        await DapperSchema.EnsureCreatedAsync(connection);
        // The hosted service runs this on every boot, so a second pass has to be a no-op.
        await DapperSchema.EnsureCreatedAsync(connection);

        var tables = (await connection.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")).ToList();

        Assert.Equal(["Languages", "Translations"], tables);
    }

    [Fact]
    public async Task EnsureCreated_LeavesAClosedConnectionClosed()
    {
        // Not added to _owned: this one is never opened, and the point is that it stays that way.
        using var connection = new SqliteConnection("DataSource=:memory:");

        await DapperSchema.EnsureCreatedAsync(connection);

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task EnsureCreated_HonoursTableNaming()
    {
        var connection = await NewConnectionAsync();
        var options = new DapperOptions { TranslationsTable = "AppTranslations", LanguagesTable = "AppLanguages" };

        await DapperSchema.EnsureCreatedAsync(connection, options);
        await new DapperLanguageStore(connection, options).AddAsync(new Language { Code = "en", Name = "English", IsDefault = true });
        await new DapperTranslationStore(connection, options).UpsertManyAsync(
            [new Translation { EntityName = "Product", EntityId = "1", PropertyName = "Name", LanguageCode = "en", Value = "Laptop" }]);

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AppTranslations"));
    }

    [Theory]
    [InlineData(SqlDialect.SqlServer)]
    [InlineData(SqlDialect.PostgreSql)]
    [InlineData(SqlDialect.Sqlite)]
    public void Script_MatchesTheEfCoreProvidersShape(SqlDialect dialect)
    {
        var script = DapperSchema.Script(dialect);

        // The names the EF Core provider generates for the same model. A database created here and
        // one created by a migration should be comparable object for object.
        Assert.Contains("PK_Translations", script, StringComparison.Ordinal);
        Assert.Contains("PK_Languages", script, StringComparison.Ordinal);
        Assert.Contains("FK_Translations_Languages_LanguageCode", script, StringComparison.Ordinal);
        Assert.Contains("IX_Translations_LanguageCode", script, StringComparison.Ordinal);
        Assert.Contains("IX_Languages_IsDefault", script, StringComparison.Ordinal);
        // EF Core's DeleteBehavior.Restrict, as each engine's EF provider spells it.
        Assert.Contains(
            dialect == SqlDialect.SqlServer ? "ON DELETE NO ACTION" : "ON DELETE RESTRICT",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DialectOf_PrefersPostgreSqlOverSqlServer()
    {
        // "NpgsqlConnection" contains "SqlConnection"; the order of the checks is the only thing
        // keeping every Npgsql connection from being taken for SQL Server.
        Assert.Equal(SqlDialect.Sqlite, DapperStatements.DialectOf(new SqliteConnection()));
        Assert.Equal(SqlDialect.PostgreSql, DapperStatements.DialectOf(new FakeNpgsqlConnection()));
    }

    [Fact]
    public void DialectOf_UnknownConnection_Throws() =>
        Assert.Throws<NotSupportedException>(() => DapperStatements.DialectOf(new FakeOracleConnection()));

    public void Dispose()
    {
        foreach (var connection in _owned)
            connection.Dispose();
    }

    private sealed class FakeNpgsqlConnection : StubConnection;

    private sealed class FakeOracleConnection : StubConnection;

    private abstract class StubConnection : IDbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public string ConnectionString { get; set; } = string.Empty;
        public int ConnectionTimeout => 0;
        public string Database => string.Empty;
        public ConnectionState State => ConnectionState.Closed;
        public IDbTransaction BeginTransaction() => throw new NotSupportedException();
        public IDbTransaction BeginTransaction(IsolationLevel il) => throw new NotSupportedException();
        public void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public void Close() { }
        public IDbCommand CreateCommand() => throw new NotSupportedException();
        public void Dispose() { }
        public void Open() { }
    }
}
