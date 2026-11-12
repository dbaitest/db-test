using System.Data.Common;

using DbAiTest.Core.Diagnostics;
using DbAiTest.Core.Engine;
using DbAiTest.Core.Schema;

namespace DbAiTest.Core;

/// <summary>
/// The harness: one throwaway container, one schema, one set of shared primitives.
/// </summary>
/// <remarks>
/// <para>
/// A harness owns the container lifecycle and the schema, and exposes the operations every
/// database test needs regardless of engine: open a connection, seed, clean, introspect and
/// guard the shape of shared objects. Test code never mentions an engine, a container or a
/// connection string.
/// </para>
/// <para>
/// The harness is deliberately not thread-safe. A suite that shares one container shares one
/// schema, and tests that reshape shared objects will corrupt each other. Run the suite
/// sequentially — that is what a shared container requires, not a stylistic choice.
/// </para>
/// </remarks>
public sealed class DbAiTestHarness : IAsyncDisposable
{
    private readonly DbEngine _engine;
    private readonly IDbContainerHandle _container;
    private readonly SchemaShapeGuard? _guard;
    private bool _disposed;

    private DbAiTestHarness(
        DbEngine engine,
        IDbContainerHandle container,
        IReadOnlyDictionary<string, SchemaObject> baseline)
    {
        _engine = engine;
        _container = container;
        _guard = baseline.Count > 0 ? new SchemaShapeGuard(engine, baseline) : null;
    }

    /// <summary>The engine this harness drives.</summary>
    public DbEngine Engine => _engine;

    /// <summary>The connection string of the running container.</summary>
    public string ConnectionString => _container.ConnectionString;

    /// <summary>The container image in use.</summary>
    public string Image => _container.Image;

    /// <summary>The shape guard, when a schema was applied.</summary>
    public SchemaShapeGuard? Guard => _guard;

    /// <summary>
    /// Starts a container and applies a schema to it.
    /// </summary>
    /// <param name="engine">The engine to start.</param>
    /// <param name="schemaRoot">Directory holding the schema files. When null, no schema is applied.</param>
    /// <param name="containerOptions">Container options, or null for the engine defaults.</param>
    /// <param name="loadOptions">Schema load options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A ready harness.</returns>
    /// <exception cref="SchemaApplyException">The schema failed to apply.</exception>
    public static Task<DbAiTestHarness> StartAsync(
        DbEngine engine,
        string? schemaRoot = null,
        DbContainerOptions? containerOptions = null,
        SchemaLoadOptions? loadOptions = null,
        CancellationToken cancellationToken = default)
    {
        var source = string.IsNullOrEmpty(schemaRoot) ? null : new DirectorySchemaSource(schemaRoot, loadOptions);
        return StartCoreAsync(engine, source, containerOptions, cancellationToken);
    }

    /// <summary>
    /// Starts a container and puts a schema into it through a <see cref="SchemaSource"/> — a
    /// directory of SQL files, a SQL Server <c>.dacpac</c>, or a source of your own.
    /// </summary>
    /// <param name="engine">The engine to start.</param>
    /// <param name="schemaSource">The schema to apply.</param>
    /// <param name="containerOptions">Container options, or null for the engine defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A ready harness.</returns>
    /// <exception cref="DbAiTestException">The source rejected the configuration; no container was started.</exception>
    /// <exception cref="SchemaApplyException">The schema failed to apply.</exception>
    public static Task<DbAiTestHarness> StartAsync(
        DbEngine engine,
        SchemaSource schemaSource,
        DbContainerOptions? containerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schemaSource);
        return StartCoreAsync(engine, schemaSource, containerOptions, cancellationToken);
    }

    private static async Task<DbAiTestHarness> StartCoreAsync(
        DbEngine engine,
        SchemaSource? source,
        DbContainerOptions? containerOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);

        // Configuration mistakes surface before the image pull and the server boot.
        source?.Validate(engine);

        var options = containerOptions ?? new DbContainerOptions();
        var container = await engine.StartContainerAsync(options, cancellationToken).ConfigureAwait(false);

        // Everything after the container exists is inside the try, so a failure anywhere —
        // the apply, the baseline read — removes the container instead of leaking it.
        try
        {
            IReadOnlyDictionary<string, SchemaObject> baseline =
                new Dictionary<string, SchemaObject>(StringComparer.OrdinalIgnoreCase);

            if (source is not null)
            {
                var context = new SchemaApplyContext
                {
                    Engine = engine,
                    ConnectionString = container.ConnectionString,
                    Database = options.Database,
                };

                var apply = await source.ApplyAsync(context, cancellationToken).ConfigureAwait(false);
                apply.ThrowIfFailed();

                await using var connection = engine.CreateConnection(container.ConnectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                var objects = await engine.ListObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
                baseline = objects.ToDictionary(o => o.QualifiedName, StringComparer.OrdinalIgnoreCase);
            }

            return new DbAiTestHarness(engine, container, baseline);
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates a connection to the containerized database without opening it. The caller opens
    /// and disposes it.
    /// </summary>
    public DbConnection CreateConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _engine.CreateConnection(_container.ConnectionString);
    }

    /// <summary>Creates and opens a connection to the containerized database. The caller disposes it.</summary>
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Empties every user table and resets identity counters to their seed.</summary>
    public Task CleanAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteOnConnectionAsync(
            c => _engine.CleanAsync(c, cancellationToken),
            cancellationToken);
    }

    /// <summary>Runs a statement and returns the number of rows affected.</summary>
    public Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        return ExecuteOnConnectionAsync(
            c => _engine.ExecuteAsync(c, sql, cancellationToken),
            cancellationToken);
    }

    /// <summary>Runs a statement with parameters and returns the number of rows affected.</summary>
    public Task<int> ExecuteAsync(
        string sql,
        IReadOnlyList<DbParameterValue> parameters,
        CancellationToken cancellationToken = default)
    {
        return ExecuteOnConnectionAsync(
            c => _engine.ExecuteAsync(c, sql, parameters, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Runs a query and converts the first column of the first row to <typeparamref name="T"/>.
    /// SQL NULL and an empty result both give <c>default</c>.
    /// </summary>
    public Task<T?> QueryScalarAsync<T>(string sql, CancellationToken cancellationToken = default)
    {
        return ExecuteOnConnectionAsync(
            c => _engine.QueryScalarAsync<T>(c, sql, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Runs a query with parameters and converts the first column of the first row to
    /// <typeparamref name="T"/>. SQL NULL and an empty result both give <c>default</c>.
    /// </summary>
    public Task<T?> QueryScalarAsync<T>(
        string sql,
        IReadOnlyList<DbParameterValue> parameters,
        CancellationToken cancellationToken = default)
    {
        return ExecuteOnConnectionAsync(
            c => _engine.QueryScalarAsync<T>(c, sql, parameters, cancellationToken),
            cancellationToken);
    }

    /// <summary>Lists every table, view and routine in the database, as the shape guard sees them.</summary>
    public Task<IReadOnlyList<SchemaObject>> ListObjectsAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteOnConnectionAsync(
            c => _engine.ListObjectsAsync(c, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Checks whether a shared object was reshaped since the schema was applied, and blames the
    /// test named. See <see cref="SchemaShapeGuard.CheckAsync"/>.
    /// </summary>
    /// <param name="responsible">The test (or test class) to blame.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The leaks first detected by this check, empty when the schema is intact.</returns>
    public async Task<IReadOnlyList<SchemaShapeLeak>> CheckShapeAsync(
        string responsible,
        CancellationToken cancellationToken = default)
    {
        if (_guard is null)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await _guard.CheckAsync(connection, responsible, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs an action against a freshly opened connection and closes it afterwards.</summary>
    public async Task<T> ExecuteOnConnectionAsync<T>(
        Func<DbConnection, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await action(connection).ConfigureAwait(false);
    }

    /// <summary>Runs an action against a freshly opened connection and closes it afterwards.</summary>
    public async Task ExecuteOnConnectionAsync(
        Func<DbConnection, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await action(connection).ConfigureAwait(false);
    }

    /// <summary>Disposes the container, removing it and all its data.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _container.DisposeAsync().ConfigureAwait(false);
    }
}
