namespace DotNetBoost.MultiLingual.Dapper;

/// <summary>
/// Table naming for the two tables this provider reads and writes. The defaults match
/// <c>MultiLingualModelOptions</c> in the EF Core provider, so the same database serves both.
/// Configured through the <c>configure</c> argument of <c>UseDapper(...)</c>.
/// </summary>
public sealed class DapperOptions
{
    /// <summary>Table holding translation rows. Default: <c>Translations</c>.</summary>
    public string TranslationsTable { get; set; } = "Translations";

    /// <summary>Table holding languages. Default: <c>Languages</c>.</summary>
    public string LanguagesTable { get; set; } = "Languages";

    /// <summary>
    /// Schema for both tables, or <c>null</c> for the connection's default. SQLite has no
    /// schemas; leave this unset there.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Overrides the SQL dialect instead of inferring it from the connection type. Set this when
    /// the connection is a wrapper — a profiler, a resiliency decorator — whose type name no
    /// longer says which engine is behind it.
    /// </summary>
    public SqlDialect? Dialect { get; set; }
}

/// <summary>
/// The SQL dialects this provider writes. They differ in identifier quoting and in how an upsert
/// is spelled; everything else is the same statement.
/// </summary>
public enum SqlDialect
{
    /// <summary>Microsoft SQL Server. Identifiers in <c>[brackets]</c>, upserts via <c>MERGE</c>.</summary>
    SqlServer,

    /// <summary>PostgreSQL. Identifiers in <c>"quotes"</c>, upserts via <c>INSERT … ON CONFLICT</c>.</summary>
    PostgreSql,

    /// <summary>SQLite. Identifiers in <c>"quotes"</c>, upserts via <c>INSERT … ON CONFLICT</c>.</summary>
    Sqlite
}
