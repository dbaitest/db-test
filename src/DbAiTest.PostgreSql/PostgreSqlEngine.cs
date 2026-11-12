using System.Data.Common;

using DbAiTest.Core.Engine;
using DbAiTest.Core.Schema;

using Npgsql;

using Testcontainers.PostgreSql;

namespace DbAiTest.PostgreSql;

/// <summary>
/// The PostgreSQL engine.
/// </summary>
/// <remarks>
/// A PostgreSQL schema file is sent whole, as one command. A file may contain dollar-quoted
/// function bodies whose inner semicolons are not statement separators, and the server's own
/// parser handles a multi-statement command correctly — so no client-side splitting is the
/// splitting rule that cannot get dollar quoting wrong.
/// </remarks>
public sealed class PostgreSqlEngine : DbEngine
{
    /// <summary>The image used when none is specified.</summary>
    public const string DefaultContainerImage = "postgres:16-alpine";

    /// <summary>The login used when the caller does not supply one.</summary>
    private const string DefaultUsername = "dbaitest";

    /// <summary>The password used when the caller does not supply one.</summary>
    private const string DefaultPassword = "dbaitest";

    /// <summary>Tables, partitioned tables, views and materialized views, one row per column.</summary>
    private const string RelationsSql = """
        SELECT
            n.nspname AS schema_name,
            c.relname AS object_name,
            c.relkind::text AS object_kind,
            a.attname AS column_name
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
        WHERE c.relkind IN ('r', 'v', 'm', 'p')
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND n.nspname NOT LIKE 'pg_toast%'
          AND n.nspname NOT LIKE 'pg_temp%'
        ORDER BY n.nspname, c.relname, a.attnum;
        """;

    /// <summary>
    /// Routines, one row per argument name. Routines live in pg_proc, not pg_class. Overloads
    /// share a name and are ordered by their argument signature, so the combined argument list
    /// is the same on every read and the shape guard does not see a change that did not happen.
    /// </summary>
    private const string RoutinesSql = """
        SELECT
            n.nspname AS schema_name,
            p.proname AS routine_name,
            arg.name  AS argument_name
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        LEFT JOIN LATERAL unnest(p.proargnames) WITH ORDINALITY AS arg(name, ordinal) ON true
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND n.nspname NOT LIKE 'pg_toast%'
          AND n.nspname NOT LIKE 'pg_temp%'
        ORDER BY n.nspname, p.proname, pg_get_function_identity_arguments(p.oid), arg.ordinal;
        """;

    /// <summary>
    /// TRUNCATE … RESTART IDENTITY CASCADE empties every table and resets every owned sequence
    /// to its START value in one statement, whatever the foreign keys between the tables.
    /// </summary>
    private const string CleanSql = """
        DO $$
        DECLARE
            stmt text;
        BEGIN
            SELECT 'TRUNCATE TABLE ' || string_agg(quote_ident(table_schema) || '.' || quote_ident(table_name), ', ')
                   || ' RESTART IDENTITY CASCADE;'
            INTO stmt
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
              AND table_type = 'BASE TABLE';

            IF stmt IS NOT NULL THEN
                EXECUTE stmt;
            END IF;
        END $$;
        """;

    /// <inheritdoc />
    public override string Name => "postgres";

    /// <inheritdoc />
    public override string DefaultImage => DefaultContainerImage;

    /// <inheritdoc />
    public override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    /// <inheritdoc />
    public override async Task<IDbContainerHandle> StartContainerAsync(
        DbContainerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new PostgreSqlBuilder(options.Image ?? DefaultContainerImage)
            .WithDatabase(options.Database)
            .WithUsername(options.Username ?? DefaultUsername)
            .WithPassword(options.Password ?? DefaultPassword);

        foreach (var (key, value) in options.Environment)
        {
            builder = builder.WithEnvironment(key, value);
        }

        var container = builder.Build();

        try
        {
            using var startup = CreateStartupToken(options, cancellationToken);
            await container.StartAsync(startup.Token).ConfigureAwait(false);
            return new PostgreSqlContainerHandle(container);
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SchemaObject>> ListObjectsAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        var collector = new SchemaObjectCollector();

        await ReadRowsAsync(
            connection,
            RelationsSql,
            row => collector.Add(
                row.GetString(0),
                row.GetString(1),
                MapKind(row.GetString(2)[0]),
                row.IsDBNull(3) ? null : row.GetString(3)),
            cancellationToken).ConfigureAwait(false);

        // The argument names are a routine's shape: changing them changes what a caller must
        // pass, which is exactly what the shape guard watches for.
        await ReadRowsAsync(
            connection,
            RoutinesSql,
            row => collector.Add(
                row.GetString(0),
                row.GetString(1),
                SchemaObjectKind.Routine,
                row.IsDBNull(2) ? null : row.GetString(2)),
            cancellationToken).ConfigureAwait(false);

        return collector.Build();
    }

    /// <inheritdoc />
    public override async Task CleanAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(connection, CleanSql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> SplitScript(SchemaScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return string.IsNullOrWhiteSpace(script.Content) ? [] : [script.Content];
    }

    private static SchemaObjectKind MapKind(char relkind) => relkind switch
    {
        'r' or 'p' => SchemaObjectKind.Table,
        'v' or 'm' => SchemaObjectKind.View,
        _ => throw new InvalidOperationException($"Unexpected pg_class.relkind '{relkind}'."),
    };
}

/// <summary>Handle to a running PostgreSQL container.</summary>
internal sealed class PostgreSqlContainerHandle(PostgreSqlContainer container) : IDbContainerHandle
{
    public string ConnectionString { get; } = container.GetConnectionString();

    public string Image { get; } = container.Image.FullName;

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
