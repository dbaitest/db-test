using System.Data.Common;
using System.Diagnostics;

using DbAiTest.Core.Schema;

namespace DbAiTest.Core.Engine;

/// <summary>
/// Everything the harness needs to know about one database engine. One subclass per engine
/// (SQL Server, PostgreSQL, MySQL); the harness itself knows none of them.
/// </summary>
/// <remarks>
/// <para>
/// The three engines differ in ways that used to leak into test code: how a file of SQL is
/// split into batches, how a container is started, how objects are introspected and how a
/// table is emptied. Those are the abstract members. Everything that is plain ADO.NET —
/// running a statement, binding parameters, reading a scalar, applying a list of scripts and
/// collecting the failures — lives here once, so an engine only writes what is genuinely
/// engine-specific.
/// </para>
/// <para>
/// This is an abstract class rather than an interface on purpose: a new capability can be
/// added as a virtual member with a default, without breaking an engine written outside this
/// repository.
/// </para>
/// </remarks>
public abstract class DbEngine
{
    /// <summary>Short engine name, used in reports and CLI arguments (for example <c>mssql</c>).</summary>
    public abstract string Name { get; }

    /// <summary>The container image this engine runs by default.</summary>
    public abstract string DefaultImage { get; }

    /// <summary>The timeout applied to every command the engine runs. Defaults to two minutes.</summary>
    public virtual TimeSpan CommandTimeout => TimeSpan.FromMinutes(2);

    /// <summary>Creates a connection to the given connection string. The connection is not opened.</summary>
    public abstract DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Starts a throwaway container and returns a handle to it. Implementations honour
    /// <see cref="DbContainerOptions.StartupTimeout"/>, usually through <see cref="CreateStartupToken"/>.
    /// </summary>
    /// <param name="options">Container options, including an image override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public abstract Task<IDbContainerHandle> StartContainerAsync(
        DbContainerOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the objects the harness can see, used for shape guarding and reporting.</summary>
    public abstract Task<IReadOnlyList<SchemaObject>> ListObjectsAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Empties every user table and resets identity counters to their seed, whatever the
    /// foreign keys between the tables.
    /// </summary>
    public abstract Task CleanAsync(DbConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Splits one schema file into the commands the server accepts, in order. This is where
    /// the dialects differ most: <c>GO</c> batches, <c>DELIMITER</c> directives, dollar quoting.
    /// </summary>
    protected abstract IReadOnlyList<string> SplitScript(SchemaScript script);

    /// <summary>
    /// Applies schema scripts to the database, one file at a time.
    /// </summary>
    /// <remarks>
    /// A failing file does not stop the run: every failure is collected so one run reports
    /// every broken file. Call <see cref="SchemaApplyResult.ThrowIfFailed"/> on the result.
    /// </remarks>
    /// <param name="connection">An open connection to the target database.</param>
    /// <param name="scripts">Scripts to apply, in order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the apply, including any per-file failures.</returns>
    public virtual async Task<SchemaApplyResult> ApplySchemaAsync(
        DbConnection connection,
        IReadOnlyList<SchemaScript> scripts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(scripts);

        var applied = new List<string>();
        var failures = new List<SchemaApplyFailure>();
        var stopwatch = Stopwatch.StartNew();

        foreach (var script in scripts)
        {
            try
            {
                foreach (var batch in SplitScript(script))
                {
                    await ExecuteAsync(connection, batch, [], cancellationToken).ConfigureAwait(false);
                }

                applied.Add(script.RelativePath);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                failures.Add(new SchemaApplyFailure(script.RelativePath, ex.Message));
            }
        }

        stopwatch.Stop();
        return new SchemaApplyResult(applied, failures, stopwatch.Elapsed);
    }

    /// <summary>Runs a single statement and returns the number of rows it affected.</summary>
    public Task<int> ExecuteAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(connection, sql, [], cancellationToken);
    }

    /// <summary>Runs a single statement with parameters and returns the number of rows it affected.</summary>
    public virtual async Task<int> ExecuteAsync(
        DbConnection connection,
        string sql,
        IReadOnlyList<DbParameterValue> parameters,
        CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a query and converts the first column of the first row to <typeparamref name="T"/>.
    /// SQL NULL and an empty result both give <c>default</c>.
    /// </summary>
    public Task<T?> QueryScalarAsync<T>(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken = default)
    {
        return QueryScalarAsync<T>(connection, sql, [], cancellationToken);
    }

    /// <summary>
    /// Runs a query with parameters and converts the first column of the first row to
    /// <typeparamref name="T"/>. SQL NULL and an empty result both give <c>default</c>.
    /// </summary>
    /// <remarks>
    /// The conversion tolerates the differences between drivers: a <c>COUNT(*)</c> that comes
    /// back as <c>long</c> converts to <c>int</c>, a nullable target such as <c>int?</c> works,
    /// a MySQL <c>CHAR(36)</c> converts to <see cref="Guid"/>, and a <see cref="DateTime"/>
    /// converts to <see cref="DateTimeOffset"/>.
    /// </remarks>
    public virtual async Task<T?> QueryScalarAsync<T>(
        DbConnection connection,
        string sql,
        IReadOnlyList<DbParameterValue> parameters,
        CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return ScalarConverter.Convert<T>(value);
    }

    /// <summary>
    /// Creates a command with the engine's timeout and the given parameters bound. The caller
    /// disposes it.
    /// </summary>
    protected virtual DbCommand CreateCommand(
        DbConnection connection,
        string sql,
        IReadOnlyList<DbParameterValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(parameters);

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = (int)CommandTimeout.TotalSeconds;

        foreach (var parameter in parameters)
        {
            var dbParameter = command.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            command.Parameters.Add(dbParameter);
        }

        return command;
    }

    /// <summary>
    /// Runs a query and hands every row to <paramref name="onRow"/>. The helper engines use
    /// to read their catalog views.
    /// </summary>
    protected async Task ReadRowsAsync(
        DbConnection connection,
        string sql,
        Action<DbDataReader> onRow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onRow);

        await using var command = CreateCommand(connection, sql, []);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            onRow(reader);
        }
    }

    /// <summary>
    /// A token that cancels when the caller cancels or when <see cref="DbContainerOptions.StartupTimeout"/>
    /// elapses, whichever comes first. The caller disposes the source.
    /// </summary>
    protected static CancellationTokenSource CreateStartupToken(
        DbContainerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(options.StartupTimeout);
        return source;
    }
}
