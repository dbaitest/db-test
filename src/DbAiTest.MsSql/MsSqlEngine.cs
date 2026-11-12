using System.Data.Common;

using DbAiTest.Core.Engine;
using DbAiTest.Core.Schema;

using Microsoft.Data.SqlClient;

using Testcontainers.MsSql;

namespace DbAiTest.MsSql;

/// <summary>
/// The SQL Server engine.
/// </summary>
/// <remarks>
/// <para>
/// Two SQL Server specifics shape this implementation. A schema file is a sequence of batches
/// separated by <c>GO</c>, which is a client directive the server rejects, so files are split
/// on it before they reach the wire. A batch may hold several statements, and SQL Server
/// accepts a batch, so a batch is sent whole rather than split on semicolons — which also
/// keeps a <c>CREATE PROCEDURE</c> body intact.
/// </para>
/// <para>
/// The Testcontainers SQL Server builder always logs in as <c>sa</c> and always starts on
/// <c>master</c>: it exposes no way to choose either. The target database is therefore
/// created here after the container is up, and the connection string is derived from the
/// container's own, so the Docker host (local, remote or Docker-in-Docker) is the one
/// Testcontainers resolved.
/// </para>
/// </remarks>
public sealed class MsSqlEngine : DbEngine
{
    /// <summary>The image used when none is specified.</summary>
    public const string DefaultContainerImage = "mcr.microsoft.com/mssql/server:2022-latest";

    /// <summary>The login the SQL Server container always uses.</summary>
    private const string Login = "sa";

    /// <summary>
    /// The password the SQL Server container uses when the caller does not supply one.
    /// SQL Server enforces a password policy at startup: a simple password fails the server
    /// before it accepts a single connection.
    /// </summary>
    private const string DefaultPassword = "Password!1";

    /// <summary>
    /// Empties every table and puts every identity column back to its seed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table no foreign key references is TRUNCATEd: that empties it and resets its identity
    /// to exactly the declared seed, whatever the column type. SQL Server refuses to TRUNCATE a
    /// table a foreign key references (even a disabled one), so those tables are emptied with
    /// DELETE while every constraint is disabled, and the constraints are re-enabled
    /// <c>WITH CHECK</c> so the optimizer still trusts them.
    /// </para>
    /// <para>
    /// A DELETEd table's identity is reseeded, and the value depends on its history:
    /// <c>DBCC CHECKIDENT … RESEED, n</c> makes the next row <c>n + increment</c> when the table
    /// has ever held a row, but <c>n</c> itself when it never has (<c>last_value</c> is NULL).
    /// A fixed 0 therefore gave a never-used table a first id of 0 while every other engine
    /// gives 1. When <c>seed − increment</c> is outside the column's type — a referenced
    /// <c>tinyint IDENTITY(0,1)</c> that has held rows — no reseed can make the next id the
    /// seed, so the clean fails with a message naming the table instead of DBCC's
    /// "Parameter 3 is incorrect".
    /// </para>
    /// </remarks>
    private const string CleanSql = """
        DECLARE @sql nvarchar(max) = N'';
        DECLARE @referenced TABLE (object_id int PRIMARY KEY);

        INSERT INTO @referenced (object_id)
        SELECT DISTINCT referenced_object_id FROM sys.foreign_keys;

        SELECT @sql = @sql + N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' NOCHECK CONSTRAINT ALL;' + CHAR(10)
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0;

        SELECT @sql = @sql
            + CASE WHEN r.object_id IS NULL THEN N'TRUNCATE TABLE ' ELSE N'DELETE FROM ' END
            + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';' + CHAR(10)
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        LEFT JOIN @referenced r ON r.object_id = t.object_id
        WHERE t.is_ms_shipped = 0;

        SELECT @sql = @sql + N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' WITH CHECK CHECK CONSTRAINT ALL;' + CHAR(10)
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0;

        SELECT @sql = @sql
            + N'BEGIN TRY DBCC CHECKIDENT (N''' + REPLACE(QUOTENAME(s.name) + N'.' + QUOTENAME(t.name), N'''', N'''''')
            + N''', RESEED, '
            + CAST(CASE WHEN ic.last_value IS NULL
                        THEN CAST(ic.seed_value AS decimal(38, 0))
                        ELSE CAST(ic.seed_value AS decimal(38, 0)) - CAST(ic.increment_value AS decimal(38, 0))
                   END AS nvarchar(40))
            + N') WITH NO_INFOMSGS; END TRY BEGIN CATCH THROW 50000, N'''
            + REPLACE(
                N'DbAiTest cannot reset the identity of ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name)
                    + N' to its seed: a foreign key references the table, so it cannot be truncated, '
                    + N'and seed minus increment is outside the range of the column type.',
                N'''', N'''''')
            + N''', 1; END CATCH;' + CHAR(10)
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.identity_columns ic ON ic.object_id = t.object_id
        JOIN @referenced r ON r.object_id = t.object_id
        WHERE t.is_ms_shipped = 0;

        EXEC sp_executesql @sql;
        """;

    private const string ListObjectsSql = """
        SELECT
            s.name       AS schema_name,
            o.name       AS object_name,
            RTRIM(CAST(o.type AS NVARCHAR(2))) AS object_type,
            c.name       AS column_name
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN sys.columns c ON c.object_id = o.object_id
        WHERE o.is_ms_shipped = 0
          AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
        ORDER BY s.name, o.name, c.column_id;
        """;

    /// <inheritdoc />
    public override string Name => "mssql";

    /// <inheritdoc />
    public override string DefaultImage => DefaultContainerImage;

    /// <inheritdoc />
    public override DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    /// <inheritdoc />
    /// <exception cref="ArgumentException">A login other than <c>sa</c> was requested.</exception>
    public override async Task<IDbContainerHandle> StartContainerAsync(
        DbContainerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Username is not null && !string.Equals(options.Username, Login, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The SQL Server container always logs in as '{Login}'; '{options.Username}' cannot be used.",
                nameof(options));
        }

        var builder = new MsSqlBuilder(options.Image ?? DefaultContainerImage)
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

            var master = new SqlConnectionStringBuilder(container.GetConnectionString())
            {
                InitialCatalog = "master",
                TrustServerCertificate = true,
                Encrypt = false,
            };

            await using (var connection = new SqlConnection(master.ConnectionString))
            {
                await connection.OpenAsync(startup.Token).ConfigureAwait(false);

                // CREATE DATABASE takes no parameter, so the name is escaped as an identifier
                // and the existence check is parameterised.
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    IF DB_ID(@database) IS NULL
                    BEGIN
                        EXEC(N'CREATE DATABASE {QuoteIdentifierForDynamicSql(options.Database)}');
                    END
                    """;
                command.Parameters.Add(new SqlParameter("@database", options.Database));
                await command.ExecuteNonQueryAsync(startup.Token).ConfigureAwait(false);
            }

            var target = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = options.Database };
            return new MsSqlContainerHandle(container, target.ConnectionString);
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
            ListObjectsSql,
            row => collector.Add(
                row.GetString(0),
                row.GetString(1),
                MapKind(row.GetString(2)),
                row.IsDBNull(3) ? null : row.GetString(3)),
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
        return SqlStatementSplitter.SplitOnGo(script.Content);
    }

    /// <summary>
    /// Quotes a database name as <c>[name]</c>, and doubles the single quotes as well because
    /// the result is embedded in an <c>N'…'</c> literal passed to <c>EXEC</c>.
    /// </summary>
    private static string QuoteIdentifierForDynamicSql(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal) + "]";

    private static SchemaObjectKind MapKind(string sqlServerType) => sqlServerType switch
    {
        "U" => SchemaObjectKind.Table,
        "V" => SchemaObjectKind.View,
        _ => SchemaObjectKind.Routine,
    };
}

/// <summary>Handle to a running SQL Server container.</summary>
internal sealed class MsSqlContainerHandle(MsSqlContainer container, string connectionString) : IDbContainerHandle
{
    public string ConnectionString { get; } = connectionString;

    public string Image { get; } = container.Image.FullName;

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
