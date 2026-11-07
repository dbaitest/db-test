using System.Data.Common;

using DbTest.Core.Engine;
using DbTest.Core.Schema;

using MySqlConnector;
using Testcontainers.MySql;

namespace DbTest.MySql;

/// <summary>
/// The MySQL engine.
/// </summary>
/// <remarks>
/// MySQL's schema files use the <c>DELIMITER</c> directive to let a stored-routine body carry
/// semicolons. That directive is understood by the mysql client, not by the server, so the
/// splitter honours it and sends each routine body as one statement.
/// </remarks>
public sealed class MySqlEngine : IDbEngine
{
    /// <summary>The image used when none is specified.</summary>
    public const string DefaultContainerImage = "mysql:8.4";

    /// <summary>The login used when the caller does not supply one.</summary>
    private const string DefaultUsername = "dbtest";

    /// <summary>The password used when the caller does not supply one.</summary>
    private const string DefaultPassword = "dbtest";

    /// <inheritdoc />
    public string Name => "mysql";

    /// <inheritdoc />
    public string DefaultImage => DefaultContainerImage;

    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    /// <inheritdoc />
    public async Task<IDbContainerHandle> StartContainerAsync(
        DbContainerOptions options,
        CancellationToken cancellationToken = default)
    {
        var builder = new MySqlBuilder(options.Image ?? DefaultContainerImage)
            .WithDatabase(options.Database)
            .WithUsername(options.Username ?? DefaultUsername)
            .WithPassword(options.Password ?? DefaultPassword);

        foreach (var (key, value) in options.Environment)
        {
            builder = builder.WithEnvironment(key, value);
        }

        var container = builder.Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        return new MySqlContainerHandle(container);
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
                var statements = SqlStatementSplitter.SplitRespectingDelimiter(script.Content);

                foreach (var statement in statements)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    command.CommandTimeout = 120;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

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
        const string tablesSql = """
            SELECT
                t.table_schema AS schema_name,
                t.table_name   AS object_name,
                t.table_type   AS object_type,
                c.column_name  AS column_name,
                c.ordinal_position AS ordinal_position
            FROM information_schema.tables t
            LEFT JOIN information_schema.columns c
                   ON c.table_schema = t.table_schema
                  AND c.table_name = t.table_name
            WHERE t.table_schema NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
            ORDER BY t.table_schema, t.table_name, c.ordinal_position;
            """;

        // Stored procedures and functions live in information_schema.routines, not in
        // information_schema.tables.
        const string routinesSql = """
            SELECT
                r.routine_schema AS schema_name,
                r.routine_name   AS routine_name,
                p.parameter_name AS parameter_name,
                p.ordinal_position AS ordinal_position
            FROM information_schema.routines r
            LEFT JOIN information_schema.parameters p
                   ON p.specific_schema = r.routine_schema
                  AND p.specific_name = r.routine_name
            WHERE r.routine_schema NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
            ORDER BY r.routine_schema, r.routine_name, p.ordinal_position;
            """;

        var objects = new Dictionary<string, SchemaObjectBuilder>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = tablesSql;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                var type = reader.GetString(2);
                var key = $"{schema}.{name}";

                if (!objects.TryGetValue(key, out var builder))
                {
                    builder = new SchemaObjectBuilder(schema, name, MapKind(type));
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

                if (!reader.IsDBNull(2))
                {
                    builder.Columns.Add(reader.GetString(2));
                }
            }
        }

        return objects.Values.Select(b => b.Build()).ToList();
    }

    /// <inheritdoc />
    public async Task CleanAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        // Foreign key checks are disabled for the truncation: MySQL refuses to truncate a
        // parent table while a child references it, and the container is discarded afterwards.
        // The table list is read first, then truncated in a separate command: a reader and a
        // non-query command cannot share one connection.
        const string listSql = """
            SELECT CONCAT('`', table_schema, '`.`', table_name, '`')
            FROM information_schema.tables
            WHERE table_schema NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
              AND table_type = 'BASE TABLE';
            """;

        var tables = new List<string>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = listSql;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }

        if (tables.Count == 0)
        {
            return;
        }

        var statements = new List<string> { "SET FOREIGN_KEY_CHECKS = 0;" };
        statements.AddRange(tables.Select(t => $"TRUNCATE TABLE {t};"));
        statements.Add("SET FOREIGN_KEY_CHECKS = 1;");

        await using var batch = connection.CreateCommand();
        batch.CommandText = string.Join(Environment.NewLine, statements);
        batch.CommandTimeout = 120;
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static SchemaObjectKind MapKind(string tableType) => tableType switch
    {
        "BASE TABLE" => SchemaObjectKind.Table,
        "VIEW" => SchemaObjectKind.View,
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

/// <summary>Handle to a running MySQL container.</summary>
internal sealed class MySqlContainerHandle : IDbContainerHandle
{
    private readonly MySqlContainer _container;

    public MySqlContainerHandle(MySqlContainer container)
    {
        _container = container;
        ConnectionString = container.GetConnectionString();
        Image = container.Image.FullName;
    }

    public string ConnectionString { get; }

    public string Image { get; }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);
}
