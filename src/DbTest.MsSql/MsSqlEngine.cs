using System.Data.Common;

using DbTest.Core.Engine;
using DbTest.Core.Schema;

using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace DbTest.MsSql;

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
/// created here after the container is up, and the connection string is assembled from the
/// mapped port.
/// </para>
/// </remarks>
public sealed class MsSqlEngine : IDbEngine
{
    /// <summary>The image used when none is specified.</summary>
    public const string DefaultContainerImage = "mcr.microsoft.com/mssql/server:2022-latest";

    /// <summary>The port SQL Server listens on inside the container.</summary>
    private const int ContainerPort = 1433;

    /// <summary>The login the SQL Server container always uses.</summary>
    private const string Login = "sa";

    /// <summary>
    /// The password the SQL Server container uses when the caller does not supply one.
    /// SQL Server enforces a password policy at startup: a simple password fails the server
    /// before it accepts a single connection.
    /// </summary>
    private const string DefaultPassword = "Password!1";

    /// <inheritdoc />
    public string Name => "mssql";

    /// <inheritdoc />
    public string DefaultImage => DefaultContainerImage;

    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    /// <inheritdoc />
    public async Task<IDbContainerHandle> StartContainerAsync(
        DbContainerOptions options,
        CancellationToken cancellationToken = default)
    {
        var password = options.Password ?? DefaultPassword;

        var builder = new MsSqlBuilder(options.Image ?? DefaultContainerImage)
            .WithPassword(password);

        foreach (var (key, value) in options.Environment)
        {
            builder = builder.WithEnvironment(key, value);
        }

        var container = builder.Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var port = container.GetMappedPublicPort(ContainerPort);
            var masterConnectionString = BuildConnectionString(port, "master", password);

            await using (var connection = new SqlConnection(masterConnectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    IF DB_ID(@database) IS NULL
                    BEGIN
                        EXEC('CREATE DATABASE [{Escape(options.Database)}]');
                    END
                    """;
                command.Parameters.AddWithValue("@database", options.Database);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var connectionString = BuildConnectionString(port, options.Database, password);
            return new MsSqlContainerHandle(container, connectionString);
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
            var batches = SqlStatementSplitter.SplitOnGo(script.Content);

            try
            {
                foreach (var batch in batches)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = batch;
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
        const string sql = """
            SELECT
                s.name       AS schema_name,
                o.name       AS object_name,
                RTRIM(CAST(o.type AS NVARCHAR(2))) AS object_type,
                c.name       AS column_name,
                c.column_id  AS column_id
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.columns c ON c.object_id = o.object_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
            ORDER BY s.name, o.name, c.column_id;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var objects = new Dictionary<string, SchemaObjectBuilder>(StringComparer.OrdinalIgnoreCase);

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

        return objects.Values.Select(b => b.Build()).ToList();
    }

    /// <inheritdoc />
    public async Task CleanAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        // Emptied in one batch. SQL Server refuses to TRUNCATE a table that another table
        // references, and emptying them in dependency order means walking the foreign key
        // graph. Disabling the constraints for the duration of the batch is the standard way
        // to do this inside a throwaway container.
        const string sql = """
            DECLARE @sql nvarchar(max) = N'';

            SELECT @sql = @sql + N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' NOCHECK CONSTRAINT ALL;' + CHAR(13)
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id;

            SELECT @sql = @sql + N'DELETE FROM ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';' + CHAR(13)
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id;

            SELECT @sql = @sql + N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' CHECK CONSTRAINT ALL;' + CHAR(13)
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id;

            SELECT @sql = @sql + N'DBCC CHECKIDENT (' + QUOTENAME(s.name + N'.' + t.name) + N', RESEED, 0);' + CHAR(13)
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE EXISTS (SELECT 1 FROM sys.identity_columns ic WHERE ic.object_id = t.object_id);

            EXEC sp_executesql @sql;
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
        return value is null or DBNull
            ? default
            : (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string BuildConnectionString(int port, string database, string password) =>
        $"Server=127.0.0.1,{port};Database={database};User Id={Login};Password={password};TrustServerCertificate=True;Encrypt=False";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);

    private static SchemaObjectKind MapKind(string sqlServerType) => sqlServerType switch
    {
        "U" => SchemaObjectKind.Table,
        "V" => SchemaObjectKind.View,
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

/// <summary>Handle to a running SQL Server container.</summary>
internal sealed class MsSqlContainerHandle : IDbContainerHandle
{
    private readonly MsSqlContainer _container;

    public MsSqlContainerHandle(MsSqlContainer container, string connectionString)
    {
        _container = container;
        ConnectionString = connectionString;
        Image = container.Image.FullName;
    }

    public string ConnectionString { get; }

    public string Image { get; }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
