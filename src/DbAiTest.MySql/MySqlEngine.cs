using System.Data.Common;

using DbAiTest.Core.Engine;
using DbAiTest.Core.Schema;

using MySqlConnector;

using Testcontainers.MySql;

namespace DbAiTest.MySql;

/// <summary>
/// The MySQL engine.
/// </summary>
/// <remarks>
/// MySQL's schema files use the <c>DELIMITER</c> directive to let a stored-routine body carry
/// semicolons. That directive is understood by the mysql client, not by the server, so the
/// splitter honours it and sends each statement — a whole routine body included — on its own.
/// </remarks>
public sealed class MySqlEngine : DbEngine
{
    /// <summary>The image used when none is specified.</summary>
    public const string DefaultContainerImage = "mysql:8.4";

    /// <summary>The login used when the caller does not supply one.</summary>
    private const string DefaultUsername = "dbaitest";

    /// <summary>The password used when the caller does not supply one.</summary>
    private const string DefaultPassword = "dbaitest";

    private const string SystemSchemas = "('information_schema', 'mysql', 'performance_schema', 'sys')";

    private const string TablesSql = $"""
        SELECT
            t.table_schema AS schema_name,
            t.table_name   AS object_name,
            t.table_type   AS object_type,
            c.column_name  AS column_name
        FROM information_schema.tables t
        LEFT JOIN information_schema.columns c
               ON c.table_schema = t.table_schema
              AND c.table_name = t.table_name
        WHERE t.table_schema NOT IN {SystemSchemas}
        ORDER BY t.table_schema, t.table_name, c.ordinal_position;
        """;

    /// <summary>
    /// Stored procedures and functions live in information_schema.routines, not in
    /// information_schema.tables. A function's return value is a parameter row with a NULL
    /// name; it registers the routine without adding a column.
    /// </summary>
    private const string RoutinesSql = $"""
        SELECT
            r.routine_schema AS schema_name,
            r.routine_name   AS routine_name,
            p.parameter_name AS parameter_name
        FROM information_schema.routines r
        LEFT JOIN information_schema.parameters p
               ON p.specific_schema = r.routine_schema
              AND p.specific_name = r.routine_name
        WHERE r.routine_schema NOT IN {SystemSchemas}
        ORDER BY r.routine_schema, r.routine_name, p.ordinal_position;
        """;

    /// <summary>Every base table, quoted as an identifier with embedded backticks doubled.</summary>
    private const string ListTablesSql = $"""
        SELECT CONCAT('`', REPLACE(table_schema, '`', '``'), '`.`', REPLACE(table_name, '`', '``'), '`')
        FROM information_schema.tables
        WHERE table_schema NOT IN {SystemSchemas}
          AND table_type = 'BASE TABLE';
        """;

    /// <inheritdoc />
    public override string Name => "mysql";

    /// <inheritdoc />
    public override string DefaultImage => DefaultContainerImage;

    /// <inheritdoc />
    public override DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    /// <inheritdoc />
    public override async Task<IDbContainerHandle> StartContainerAsync(
        DbContainerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new MySqlBuilder(options.Image ?? DefaultContainerImage)
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
            return new MySqlContainerHandle(container);
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
            TablesSql,
            row => collector.Add(
                row.GetString(0),
                row.GetString(1),
                MapKind(row.GetString(2)),
                row.IsDBNull(3) ? null : row.GetString(3)),
            cancellationToken).ConfigureAwait(false);

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
    /// <remarks>
    /// Foreign key checks are disabled for the truncation: MySQL refuses to truncate a parent
    /// table while a child references it. The setting is per session, so it is switched off and
    /// back on inside the same command as the truncates. TRUNCATE also resets AUTO_INCREMENT to 1:
    /// MySQL keeps no separate seed, so a table created with AUTO_INCREMENT = n restarts at 1.
    /// </remarks>
    public override async Task CleanAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        var tables = new List<string>();
        await ReadRowsAsync(connection, ListTablesSql, row => tables.Add(row.GetString(0)), cancellationToken)
            .ConfigureAwait(false);

        if (tables.Count == 0)
        {
            return;
        }

        var statements = new List<string> { "SET FOREIGN_KEY_CHECKS = 0;" };
        statements.AddRange(tables.Select(t => $"TRUNCATE TABLE {t};"));
        statements.Add("SET FOREIGN_KEY_CHECKS = 1;");

        await ExecuteAsync(connection, string.Join('\n', statements), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> SplitScript(SchemaScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return SqlStatementSplitter.SplitRespectingDelimiter(script.Content);
    }

    private static SchemaObjectKind MapKind(string tableType) =>
        tableType.Contains("VIEW", StringComparison.OrdinalIgnoreCase) ? SchemaObjectKind.View : SchemaObjectKind.Table;
}

/// <summary>Handle to a running MySQL container.</summary>
internal sealed class MySqlContainerHandle(MySqlContainer container) : IDbContainerHandle
{
    public string ConnectionString { get; } = container.GetConnectionString();

    public string Image { get; } = container.Image.FullName;

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
