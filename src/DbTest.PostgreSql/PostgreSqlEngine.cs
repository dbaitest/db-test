using System.Data.Common;

using DbTest.Core.Engine;
using DbTest.Core.Schema;

using Npgsql;
using Testcontainers.PostgreSql;

namespace DbTest.PostgreSql;

/// <summary>
/// The PostgreSQL engine.
/// </summary>
/// <remarks>
/// Postgres differs from SQL Server in two ways that matter to schema application. A file may
/// contain a dollar-quoted function body whose inner semicolons must not be treated as
/// statement separators, so the splitter tracks dollar quoting. And a file is normally applied
/// as one command, because a <c>CREATE FUNCTION ... $$ ... $$</c> body is only valid as a
/// single statement.
/// </remarks>
public sealed class PostgreSqlEngine : IDbEngine
{
    /// <summary>The image used when none is specified.</summary>
    public const string DefaultContainerImage = "postgres:16-alpine";

    /// <summary>The login used when the caller does not supply one.</summary>
    private const string DefaultUsername = "dbtest";

    /// <summary>The password used when the caller does not supply one.</summary>
    private const string DefaultPassword = "dbtest";

    /// <inheritdoc />
    public string Name => "postgres";

    /// <inheritdoc />
    public string DefaultImage => DefaultContainerImage;

    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    /// <inheritdoc />
    public async Task<IDbContainerHandle> StartContainerAsync(
        DbContainerOptions options,
        CancellationToken cancellationToken = default)
    {
        var builder = new PostgreSqlBuilder(options.Image ?? DefaultContainerImage)
            .WithDatabase(options.Database)
            .WithUsername(options.Username ?? DefaultUsername)
            .WithPassword(options.Password ?? DefaultPassword);

        foreach (var (key, value) in options.Environment)
        {
            builder = builder.WithEnvironment(key, value);
        }

        var container = builder.Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        return new PostgreSqlContainerHandle(container);
    }

    /// <inheritdoc />
    public async Task<SchemaApplyResult> ApplySchemaAsync(
        DbConnection connection,
        IReadOnlyList<SchemaScript> scripts,
        CancellationToken cancellationToken = default)
    {
        var applied = new List<string>();
        var failures = new List<SchemaApplyFailure>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        foreach (var script in scripts)
        {
            try
            {
                // A file is sent whole. Splitting it would break every dollar-quoted body.
                await using var command = connection.CreateCommand();
                command.CommandText = script.Content;
                command.CommandTimeout = 120;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                applied.Add(script.RelativePath);
            }
            catch (Exception ex)
            {
                failures.Add(new SchemaApplyFailure(script.RelativePath, ex.Message));
            }
        }

        stopwatch.Stop();
        return new SchemaApplyResult(applied, failures, stopwatch.Elapsed);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SchemaObject>> ListObjectsAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        const string relationsSql = """
            SELECT
                n.nspname AS schema_name,
                c.relname AS object_name,
                c.relkind::text AS object_kind,
                a.attname AS column_name,
                a.attnum AS attnum
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            WHERE c.relkind IN ('r', 'v', 'm', 'p')
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND n.nspname NOT LIKE 'pg_toast%'
              AND n.nspname NOT LIKE 'pg_temp%'
            ORDER BY n.nspname, c.relname, a.attnum;
            """;

        // Routines live in pg_proc, not pg_class, so they need their own query.
        const string routinesSql = """
            SELECT
                n.nspname AS schema_name,
                p.proname AS routine_name,
                p.proargnames::text AS argument_names
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND n.nspname NOT LIKE 'pg_toast%'
              AND n.nspname NOT LIKE 'pg_temp%'
            ORDER BY n.nspname, p.proname;
            """;

        var objects = new Dictionary<string, SchemaObjectBuilder>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = relationsSql;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                var kindChar = reader.GetString(2)[0];
                var key = $"{schema}.{name}";

                if (!objects.TryGetValue(key, out var builder))
                {
                    builder = new SchemaObjectBuilder(schema, name, MapKind(kindChar));
                    objects[key] = builder;
                }

                if (!reader.IsDBNull(3))
                {
                    builder.Columns.Add(reader.GetString(3));
                }
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = routinesSql;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                var key = $"{schema}.{name}";

                if (!objects.TryGetValue(key, out var builder))
                {
                    builder = new SchemaObjectBuilder(schema, name, SchemaObjectKind.Routine);
                    objects[key] = builder;
                }

                // The argument names are the routine's shape: changing them changes what a
                // caller must pass, which is exactly what the shape guard watches for.
                if (!reader.IsDBNull(2))
                {
                    var raw = reader.GetString(2);
                    foreach (var argument in raw.Trim('{', '}')
                                       .Split(',', StringSplitOptions.RemoveEmptyEntries)
                                       .Select(a => a.Trim().Trim('"'))
                                       .Where(a => a.Length > 0))
                    {
                        builder.Columns.Add(argument);
                    }
                }
            }
        }

        return objects.Values.Select(b => b.Build()).ToList();
    }

    /// <inheritdoc />
    public async Task CleanAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        // TRUNCATE ... CASCADE empties the table and every table that references it, and
        // RESTART IDENTITY resets the sequence. CASCADE is safe here because the container is
        // discarded after the run. The table list is aggregated into one statement: TRUNCATE
        // takes a comma-separated list, and truncating one table per statement would need the
        // tables ordered by dependency.
        const string sql = """
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

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<int> ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(connection, sql, [], cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(
        DbConnection connection,
        string sql,
        IReadOnlyList<DbParameterValue> parameters,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;

        foreach (var parameter in parameters)
        {
            var dbParameter = command.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            command.Parameters.Add(dbParameter);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<T?> QuerySingleAsync<T>(DbConnection connection, string sql, CancellationToken cancellationToken = default)
    {
        return QuerySingleAsync<T>(connection, sql, [], cancellationToken);
    }

    /// <inheritdoc />
    public async Task<T?> QuerySingleAsync<T>(
        DbConnection connection,
        string sql,
        IReadOnlyList<DbParameterValue> parameters,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;

        foreach (var parameter in parameters)
        {
            var dbParameter = command.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            command.Parameters.Add(dbParameter);
        }

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SchemaObjectKind MapKind(char kind) => kind switch
    {
        'r' or 'p' => SchemaObjectKind.Table,
        'v' => SchemaObjectKind.View,
        _ => SchemaObjectKind.Routine,
    };

    private sealed class SchemaObjectBuilder(string schema, string name, SchemaObjectKind kind)
    {
        public string Schema { get; } = schema;

        public string Name { get; } = name;

        public SchemaObjectKind Kind { get; } = kind;

        public List<string> Columns { get; } = [];

        public SchemaObject Build() => new(Schema, Name, Kind, Columns);
    }
}

/// <summary>Handle to a running PostgreSQL container.</summary>
internal sealed class PostgreSqlContainerHandle : IDbContainerHandle
{
    private readonly PostgreSqlContainer _container;

    public PostgreSqlContainerHandle(PostgreSqlContainer container)
    {
        _container = container;
        ConnectionString = container.GetConnectionString();
        Image = container.Image.FullName;
    }

    public string ConnectionString { get; }

    public string Image { get; }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);
}
