using Dapper;
using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Metadata;
using System.Data;
using System.Data.Common;

namespace DotNetBoost.MultiLingual.Dapper;

/// <summary>
/// Creates the two tables this provider reads and writes, for applications that have no
/// migrations of their own to hang them off. Enabled with <c>UseDapper(..., migrateSchema: true)</c>,
/// which runs it once when the host starts; call <see cref="EnsureCreatedAsync"/> directly to run
/// it from a deployment step or a test instead.
/// <para>
/// The shape is the one the EF Core provider maps — same table and column names, same lengths,
/// same composite primary key, same restricting foreign key — so a database created here is the
/// database that provider expects, and an application can move between them. Column lengths come
/// from the same Core constants that provider's configuration uses, so the two cannot drift.
/// </para>
/// <para>
/// Every statement is conditional, so running it against an existing database is a no-op. It only
/// ever CREATEs: it will not alter or drop a table that is already there, which means it can bring
/// a database into existence but cannot migrate one whose shape has changed. That is the job of
/// whatever owns the rest of your schema.
/// </para>
/// </summary>
public static class DapperSchema
{
    /// <summary>
    /// Creates the <c>Languages</c> and <c>Translations</c> tables and their indexes if they are
    /// absent. Idempotent.
    /// </summary>
    /// <param name="connection">
    /// The connection to run the DDL on. Opened for the duration if it is closed, and left as it
    /// was found. Its runtime type selects the dialect unless <see cref="DapperOptions.Dialect"/>
    /// says otherwise.
    /// </param>
    /// <param name="options">Table naming. Defaults to <see cref="DapperOptions"/>'s own defaults.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <exception cref="NotSupportedException">The connection type matches no supported dialect.</exception>
    /// <exception cref="InvalidOperationException">The DDL failed — typically a role without CREATE rights.</exception>
    public static async Task EnsureCreatedAsync(
        IDbConnection connection,
        DapperOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        options ??= new DapperOptions();
        var dialect = options.Dialect ?? DapperStatements.DialectOf(connection);
        var script  = Script(dialect, options);

        var opened = await UnitOfWork.OpenIfClosedAsync(connection, ct).ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(script, cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new InvalidOperationException(
                $"Could not create the MultiLingual tables ('{options.LanguagesTable}', '{options.TranslationsTable}'). " +
                "The database role needs CREATE TABLE and CREATE INDEX rights. Pass migrateSchema: false to " +
                "UseDapper(...) if the schema is managed elsewhere.",
                ex);
        }
        finally
        {
            if (opened) connection.Close();
        }
    }

    /// <summary>
    /// The DDL <see cref="EnsureCreatedAsync"/> would run, for review or for checking into a
    /// migrations folder instead of running it at startup.
    /// </summary>
    /// <param name="dialect">The engine to spell the DDL for.</param>
    /// <param name="options">Table naming. Defaults to <see cref="DapperOptions"/>'s own defaults.</param>
    public static string Script(SqlDialect dialect, DapperOptions? options = null)
    {
        options ??= new DapperOptions();

        var languages    = DapperStatements.Qualify(dialect, options.Schema, options.LanguagesTable);
        var translations = DapperStatements.Qualify(dialect, options.Schema, options.TranslationsTable);

        string C(string column) => DapperStatements.Quote(dialect, column);

        // Lengths come from Core so the two providers cannot describe the same column differently.
        var code       = LanguageCodes.MaxLength;                    // 16
        var entityName = MultiLingualMetadata.MaxEntityNameLength;   // 128
        var entityId   = EntityKey.MaxLength;                        // 64
        const int propertyName = 128;
        const int languageName = 128;
        // "Ltr" / "Rtl": the name is stored, not the ordinal.
        const int direction = 3;

        // Index and constraint names match what EF Core generates for the same model, so the two
        // providers' databases are comparable object for object.
        var pk        = "PK_" + options.TranslationsTable;
        var pkLang    = "PK_" + options.LanguagesTable;
        var fk        = $"FK_{options.TranslationsTable}_{options.LanguagesTable}_LanguageCode";
        var fkIndex   = $"IX_{options.TranslationsTable}_LanguageCode";
        var defIndex  = $"IX_{options.LanguagesTable}_IsDefault";

        // EF Core's DeleteBehavior.Restrict, spelled the way each engine's EF provider spells it:
        // RESTRICT on PostgreSQL and SQLite, NO ACTION on SQL Server, which has no RESTRICT keyword.
        // Both refuse to delete a language that still has translations; they differ in whether the
        // check can be deferred, and a schema diff against an EF-migrated database notices.
        var onDelete = dialect == SqlDialect.SqlServer ? "NO ACTION" : "RESTRICT";

        return dialect switch
        {
            SqlDialect.SqlServer => $"""
                {SqlServerSchema(options.Schema)}
                IF OBJECT_ID(N'{languages}', N'U') IS NULL
                BEGIN
                    CREATE TABLE {languages} (
                        {C("Code")}      NVARCHAR({code})         NOT NULL,
                        {C("Name")}      NVARCHAR({languageName}) NOT NULL,
                        {C("Direction")} NVARCHAR({direction})    NOT NULL,
                        {C("IsDefault")} BIT                      NOT NULL,
                        {C("IsActive")}  BIT                      NOT NULL,
                        CONSTRAINT {C(pkLang)} PRIMARY KEY ({C("Code")})
                    );
                    CREATE INDEX {C(defIndex)} ON {languages} ({C("IsDefault")});
                END;
                IF OBJECT_ID(N'{translations}', N'U') IS NULL
                BEGIN
                    CREATE TABLE {translations} (
                        {C("EntityName")}   NVARCHAR({entityName})   NOT NULL,
                        {C("EntityId")}     NVARCHAR({entityId})     NOT NULL,
                        {C("PropertyName")} NVARCHAR({propertyName}) NOT NULL,
                        {C("LanguageCode")} NVARCHAR({code})         NOT NULL,
                        {C("Value")}        NVARCHAR(MAX)            NULL,
                        CONSTRAINT {C(pk)} PRIMARY KEY ({C("EntityName")}, {C("EntityId")}, {C("PropertyName")}, {C("LanguageCode")}),
                        CONSTRAINT {C(fk)} FOREIGN KEY ({C("LanguageCode")}) REFERENCES {languages} ({C("Code")}) ON DELETE {onDelete}
                    );
                    CREATE INDEX {C(fkIndex)} ON {translations} ({C("LanguageCode")});
                END;
                """,

            SqlDialect.PostgreSql => $"""
                {PostgreSqlSchema(dialect, options.Schema)}
                CREATE TABLE IF NOT EXISTS {languages} (
                    {C("Code")}      character varying({code})         NOT NULL,
                    {C("Name")}      character varying({languageName}) NOT NULL,
                    {C("Direction")} character varying({direction})    NOT NULL,
                    {C("IsDefault")} boolean                           NOT NULL,
                    {C("IsActive")}  boolean                           NOT NULL,
                    CONSTRAINT {C(pkLang)} PRIMARY KEY ({C("Code")})
                );
                CREATE INDEX IF NOT EXISTS {C(defIndex)} ON {languages} ({C("IsDefault")});
                CREATE TABLE IF NOT EXISTS {translations} (
                    {C("EntityName")}   character varying({entityName})   NOT NULL,
                    {C("EntityId")}     character varying({entityId})     NOT NULL,
                    {C("PropertyName")} character varying({propertyName}) NOT NULL,
                    {C("LanguageCode")} character varying({code})         NOT NULL,
                    {C("Value")}        text                              NULL,
                    CONSTRAINT {C(pk)} PRIMARY KEY ({C("EntityName")}, {C("EntityId")}, {C("PropertyName")}, {C("LanguageCode")}),
                    CONSTRAINT {C(fk)} FOREIGN KEY ({C("LanguageCode")}) REFERENCES {languages} ({C("Code")}) ON DELETE {onDelete}
                );
                CREATE INDEX IF NOT EXISTS {C(fkIndex)} ON {translations} ({C("LanguageCode")});
                """,

            // SQLite types are advisory, but they are spelled out anyway: the declared type is
            // what a schema diff against the EF Core provider's database compares.
            SqlDialect.Sqlite => $"""
                CREATE TABLE IF NOT EXISTS {languages} (
                    {C("Code")}      TEXT    NOT NULL CONSTRAINT {C(pkLang)} PRIMARY KEY,
                    {C("Name")}      TEXT    NOT NULL,
                    {C("Direction")} TEXT    NOT NULL,
                    {C("IsDefault")} INTEGER NOT NULL,
                    {C("IsActive")}  INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS {C(defIndex)} ON {languages} ({C("IsDefault")});
                CREATE TABLE IF NOT EXISTS {translations} (
                    {C("EntityName")}   TEXT NOT NULL,
                    {C("EntityId")}     TEXT NOT NULL,
                    {C("PropertyName")} TEXT NOT NULL,
                    {C("LanguageCode")} TEXT NOT NULL,
                    {C("Value")}        TEXT NULL,
                    CONSTRAINT {C(pk)} PRIMARY KEY ({C("EntityName")}, {C("EntityId")}, {C("PropertyName")}, {C("LanguageCode")}),
                    CONSTRAINT {C(fk)} FOREIGN KEY ({C("LanguageCode")}) REFERENCES {languages} ({C("Code")}) ON DELETE {onDelete}
                );
                CREATE INDEX IF NOT EXISTS {C(fkIndex)} ON {translations} ({C("LanguageCode")});
                """,

            _ => throw new NotSupportedException($"Unsupported SQL dialect: {dialect}.")
        };
    }

    /// <summary>
    /// CREATE SCHEMA has to be the first statement in its own batch on SQL Server, which a single
    /// Dapper command cannot arrange — EXEC gives it one.
    /// </summary>
    private static string SqlServerSchema(string? schema) =>
        string.IsNullOrWhiteSpace(schema)
            ? string.Empty
            : $"""
                IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{schema.Replace("'", "''", StringComparison.Ordinal)}')
                    EXEC(N'CREATE SCHEMA {DapperStatements.Quote(SqlDialect.SqlServer, schema).Replace("'", "''", StringComparison.Ordinal)}');
                """;

    private static string PostgreSqlSchema(SqlDialect dialect, string? schema) =>
        string.IsNullOrWhiteSpace(schema)
            ? string.Empty
            : $"CREATE SCHEMA IF NOT EXISTS {DapperStatements.Quote(dialect, schema)};";
}
