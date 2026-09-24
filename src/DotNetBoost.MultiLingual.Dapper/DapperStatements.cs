using System.Collections.Concurrent;
using System.Data;

namespace DotNetBoost.MultiLingual.Dapper;

/// <summary>
/// Every statement the two stores issue, spelled for one dialect and one pair of table names.
/// Built once per (dialect, schema, table names) and cached: the stores are scoped, so composing
/// a dozen strings in each constructor would be per-request work for a result that never varies.
/// </summary>
internal sealed class DapperStatements
{
    private static readonly ConcurrentDictionary<(SqlDialect, string?, string, string), DapperStatements> Cache = new();

    private DapperStatements(SqlDialect dialect, DapperOptions options)
    {
        Dialect = dialect;

        var translations = Qualify(dialect, options.Schema, options.TranslationsTable);
        var languages    = Qualify(dialect, options.Schema, options.LanguagesTable);

        string C(string column) => Quote(dialect, column);

        var columns = $"{C("EntityName")}, {C("EntityId")}, {C("PropertyName")}, {C("LanguageCode")}, {C("Value")}";
        var key     = $"{C("EntityName")} = @EntityName AND {C("EntityId")} = @EntityId AND " +
                      $"{C("PropertyName")} = @PropertyName AND {C("LanguageCode")} = @LanguageCode";

        SelectTranslations =
            $"SELECT {columns} FROM {translations} " +
            $"WHERE {C("EntityName")} = @EntityName AND {AnyOf(dialect, C("EntityId"), "@EntityIds")} " +
            $"AND {AnyOf(dialect, C("LanguageCode"), "@LanguageCodes")}";

        SelectTranslationsForEntity =
            $"SELECT {columns} FROM {translations} WHERE {C("EntityName")} = @EntityName AND {C("EntityId")} = @EntityId";

        DeleteTranslation = $"DELETE FROM {translations} WHERE {key}";

        DeleteEntities =
            $"DELETE FROM {translations} WHERE {C("EntityName")} = @EntityName AND {AnyOf(dialect, C("EntityId"), "@EntityIds")}";

        CountByLanguage  = $"SELECT COUNT(*) FROM {translations} WHERE {C("LanguageCode")} = @LanguageCode";
        DeleteByLanguage = $"DELETE FROM {translations} WHERE {C("LanguageCode")} = @LanguageCode";

        UpsertTranslation = dialect switch
        {
            // HOLDLOCK (= SERIALIZABLE) on the target is what makes MERGE safe under concurrency:
            // without it the "does this row exist" probe takes no lock, and two sessions merging
            // the same key can both decide to INSERT and one gets a duplicate-key error.
            SqlDialect.SqlServer => $"""
                MERGE {translations} WITH (HOLDLOCK) AS t
                USING (VALUES (@EntityName, @EntityId, @PropertyName, @LanguageCode, @Value))
                      AS s ({columns})
                   ON t.{C("EntityName")}   = s.{C("EntityName")}
                  AND t.{C("EntityId")}     = s.{C("EntityId")}
                  AND t.{C("PropertyName")} = s.{C("PropertyName")}
                  AND t.{C("LanguageCode")} = s.{C("LanguageCode")}
                 WHEN MATCHED THEN UPDATE SET t.{C("Value")} = s.{C("Value")}
                 WHEN NOT MATCHED THEN INSERT ({columns})
                      VALUES (s.{C("EntityName")}, s.{C("EntityId")}, s.{C("PropertyName")}, s.{C("LanguageCode")}, s.{C("Value")});
                """,

            // The conflict target is the primary key, so this is one statement and one round trip.
            _ => $"""
                INSERT INTO {translations} ({columns})
                VALUES (@EntityName, @EntityId, @PropertyName, @LanguageCode, @Value)
                ON CONFLICT ({C("EntityName")}, {C("EntityId")}, {C("PropertyName")}, {C("LanguageCode")})
                DO UPDATE SET {C("Value")} = excluded.{C("Value")}
                """
        };

        var languageColumns = $"{C("Code")}, {C("Name")}, {C("Direction")}, {C("IsDefault")}, {C("IsActive")}";

        SelectLanguages     = $"SELECT {languageColumns} FROM {languages}";
        SelectLanguage      = $"{SelectLanguages} WHERE {C("Code")} = @Code";
        InsertLanguage      = $"INSERT INTO {languages} ({languageColumns}) VALUES (@Code, @Name, @Direction, @IsDefault, @IsActive)";
        // IsDefault is deliberately absent: moving the default is SetDefaultAsync's job.
        UpdateLanguage      = $"UPDATE {languages} SET {C("Name")} = @Name, {C("Direction")} = @Direction, {C("IsActive")} = @IsActive WHERE {C("Code")} = @Code";
        DeleteLanguage      = $"DELETE FROM {languages} WHERE {C("Code")} = @Code";
        PromoteLanguage     = $"UPDATE {languages} SET {C("IsDefault")} = @True WHERE {C("Code")} = @Code";
        DemoteOtherLanguages = $"UPDATE {languages} SET {C("IsDefault")} = @False WHERE {C("Code")} <> @Code AND {C("IsDefault")} = @True";
    }

    public SqlDialect Dialect { get; }

    public string SelectTranslations { get; }
    public string SelectTranslationsForEntity { get; }
    public string UpsertTranslation { get; }
    public string DeleteTranslation { get; }
    public string DeleteEntities { get; }
    public string CountByLanguage { get; }
    public string DeleteByLanguage { get; }

    public string SelectLanguages { get; }
    public string SelectLanguage { get; }
    public string InsertLanguage { get; }
    public string UpdateLanguage { get; }
    public string DeleteLanguage { get; }
    public string PromoteLanguage { get; }
    public string DemoteOtherLanguages { get; }

    public static DapperStatements For(IDbConnection connection, DapperOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        var dialect = options.Dialect ?? DialectOf(connection);
        return Cache.GetOrAdd(
            (dialect, options.Schema, options.TranslationsTable, options.LanguagesTable),
            static (_, state) => new DapperStatements(state.Dialect, state.Options),
            (Dialect: dialect, Options: options));
    }

    /// <summary>
    /// Infers the dialect from the connection's runtime type, which is all an
    /// <see cref="IDbConnection"/> exposes about the engine behind it.
    /// </summary>
    /// <exception cref="NotSupportedException">The connection type matches no known dialect.</exception>
    public static SqlDialect DialectOf(IDbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var name = connection.GetType().Name;

        // Order matters: "NpgsqlConnection" *contains* "SqlConnection", so PostgreSQL has to be
        // matched before SQL Server or every Npgsql connection would be taken for one.
        if (name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return SqlDialect.PostgreSql;
        if (name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return SqlDialect.Sqlite;
        if (name.Contains("SqlConnection", StringComparison.OrdinalIgnoreCase)) return SqlDialect.SqlServer;

        throw new NotSupportedException(
            $"Cannot infer a SQL dialect from connection type '{connection.GetType().FullName}'. " +
            "DotNetBoost.MultiLingual.Dapper supports SQL Server, PostgreSQL and SQLite. Set " +
            "DapperOptions.Dialect explicitly if the connection is a wrapper around one of them.");
    }

    /// <summary>
    /// Matching a column against a list of values, spelled the way Dapper will have bound that
    /// list. Dapper expands a list into <c>IN (@p1, @p2, …)</c> on most providers — but on Npgsql
    /// it binds ONE array parameter instead, because PostgreSQL has real array types. An
    /// <c>IN @param</c> that works on SQL Server and SQLite is therefore a syntax error there, and
    /// only <c>= ANY(@param)</c> matches what was actually bound.
    /// </summary>
    private static string AnyOf(SqlDialect dialect, string column, string parameter) =>
        dialect == SqlDialect.PostgreSql
            ? $"{column} = ANY({parameter})"
            : $"{column} IN {parameter}";

    internal static string Quote(SqlDialect dialect, string identifier) =>
        dialect == SqlDialect.SqlServer
            ? "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]"
            : "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// A table reference, schema-qualified when a schema is configured. Identifiers are quoted in
    /// their declared casing on every dialect — PostgreSQL folds unquoted identifiers to lower
    /// case, and the EF Core provider creates them quoted and PascalCase, so an unquoted name here
    /// would look for a table that provider never created.
    /// </summary>
    internal static string Qualify(SqlDialect dialect, string? schema, string table) =>
        string.IsNullOrWhiteSpace(schema)
            ? Quote(dialect, table)
            : Quote(dialect, schema) + "." + Quote(dialect, table);
}
