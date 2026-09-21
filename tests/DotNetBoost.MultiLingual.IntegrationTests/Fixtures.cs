global using Xunit;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace DotNetBoost.MultiLingual.IntegrationTests;

/// <summary>
/// Starts a single PostgreSQL container for the whole collection. Each call to
/// <see cref="CreateDatabaseAsync"/> hands out a brand-new database on that container,
/// so every test gets a genuinely empty store without paying to boot a container.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("multilingual_root")
        .Build();

    private int _databaseCounter;

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database and returns a connection string pointing at it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        // PostgreSQL identifiers cap at 63 characters; this is 39-41, so no truncation needed.
        var name = $"test_{Interlocked.Increment(ref _databaseCounter)}_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            // Database names cannot be parameterised; the name is generated here, never user input.
            cmd.CommandText = $"CREATE DATABASE \"{name}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "postgresql";
}

/// <summary>One SQL Server container per collection, one fresh database per test.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private int _databaseCounter;

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database and returns a connection string pointing at it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"test_{Interlocked.Increment(ref _databaseCounter)}_{Guid.NewGuid():N}";

        await using (var admin = new SqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            // Database names cannot be parameterised; the name is generated here, never user input.
            cmd.CommandText = $"CREATE DATABASE [{name}]";
            await cmd.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = name }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
