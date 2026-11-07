using System.Data.Common;
using System.Diagnostics;

using DbTest.Core.Diagnostics;
using DbTest.Core.Engine;
using DbTest.Core.Schema;

namespace DbTest.Core;

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
public sealed class DbTestHarness : IAsyncDisposable
{
    private readonly IDbEngine _engine;
    private readonly IDbContainerHandle _container;
    private readonly SchemaShapeGuard? _guard;
    private bool _disposed;

    private DbTestHarness(
        IDbEngine engine,
        IDbContainerHandle container,
        IReadOnlyDictionary<string, SchemaObject> baseline)
    {
        _engine = engine;
        _container = container;
        _guard = baseline.Count > 0 ? new SchemaShapeGuard(engine, baseline) : null;
    }

    /// <summary>The engine this harness drives.</summary>
    public IDbEngine Engine => _engine;

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
    public static async Task<DbTestHarness> StartAsync(
        IDbEngine engine,
        string? schemaRoot = null,
        DbContainerOptions? containerOptions = null,
        SchemaLoadOptions? loadOptions = null,
        CancellationToken cancellationToken = default)
    {
        var options = containerOptions ?? new DbContainerOptions();
        var container = await engine.StartContainerAsync(options, cancellationToken).ConfigureAwait(false);

        try
        {
            IReadOnlyDictionary<string, SchemaObject> baseline =
                new Dictionary<string, SchemaObject>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(schemaRoot))
            {
                var load = SchemaLoader.Load(schemaRoot, loadOptions);
                await using var connection = engine.CreateConnection(container.ConnectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                var apply = await engine.ApplySchemaAsync(connection, load.Scripts, cancellationToken)
                    .ConfigureAwait(false);
                apply.ThrowIfFailed();

                baseline = await CaptureObjectsAsync(engine, connection, cancellationToken).ConfigureAwait(false);
            }

            return new DbTestHarness(engine, container, baseline);
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens a connection to the containerized database. The caller disposes it.</summary>
    public DbConnection OpenConnection()
    {
        return _engine.CreateConnection(_container.ConnectionString);
    }

    /// <summary>Opens and opens a connection asynchronously. The caller disposes it.</summary>
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Empties every user table and resets identity counters.</summary>
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

    /// <summary>Runs a query and maps the first row to <typeparamref name="T"/>.</summary>
    public Task<T?> QuerySingleAsync<T>(string sql, CancellationToken cancellationToken = default)
    {
        return ExecuteOnConnectionAsync(
            c => _engine.QuerySingleAsync<T>(c, sql, cancellationToken),
            cancellationToken);
    }

    /// <summary>Runs a query with parameters and maps the first row to <typeparamref name="T"/>.</summary>
    public Task<T?> QuerySingleAsync<T>(
        string sql,
        IReadOnlyList<DbParameterValue> parameters,
        CancellationToken cancellationToken = default)
    {
        return ExecuteOnConnectionAsync(
            c => _engine.QuerySingleAsync<T>(c, sql, parameters, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Checks whether any test reshaped a shared object, and blames the class named.
    /// </summary>
    /// <param name="responsibleClass">The test class to blame.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The leaks found, empty when the schema is intact.</returns>
    public async Task<IReadOnlyList<SchemaShapeLeak>> CheckShapeAsync(
        string responsibleClass,
        CancellationToken cancellationToken = default)
    {
        if (_guard is null)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await _guard.CheckAsync(connection, responsibleClass, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs an action against a freshly opened connection and closes it afterwards.</summary>
    public async Task<T> ExecuteOnConnectionAsync<T>(
        Func<DbConnection, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await action(connection).ConfigureAwait(false);
    }

    /// <summary>Runs an action against a freshly opened connection and closes it afterwards.</summary>
    public async Task ExecuteOnConnectionAsync(
        Func<DbConnection, Task> action,
        CancellationToken cancellationToken = default)
    {
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

    private static async Task<IReadOnlyDictionary<string, SchemaObject>> CaptureObjectsAsync(
        IDbEngine engine,
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var objects = await engine.ListObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
        return objects.ToDictionary(o => o.QualifiedName, StringComparer.OrdinalIgnoreCase);
    }
}
