using System.Data.Common;

using DbTest.Core.Schema;

namespace DbTest.Core.Engine;

/// <summary>
/// Everything the harness needs to know about one database engine. One implementation
/// per engine (SQL Server, PostgreSQL, MySQL); the harness itself knows none of them.
/// </summary>
/// <remarks>
/// The three engines differ in ways that used to leak into test code: how a file of SQL is
/// split into statements, how a container is started, how objects are introspected, how a
/// table is emptied, and how a schema is deployed. Putting all five behind this interface is
/// what lets one test suite run unchanged against all three.
/// </remarks>
public interface IDbEngine
{
    /// <summary>Short engine name, used in reports and CLI arguments (for example <c>mssql</c>).</summary>
    string Name { get; }

    /// <summary>The container image this engine runs by default.</summary>
    string DefaultImage { get; }

    /// <summary>Creates a connection to the given connection string.</summary>
    DbConnection CreateConnection(string connectionString);

    /// <summary>Starts a throwaway container and returns a handle to it.</summary>
    /// <param name="options">Container options, including an image override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IDbContainerHandle> StartContainerAsync(
        DbContainerOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies schema scripts to the database.
    /// </summary>
    /// <param name="connection">An open connection to the target database.</param>
    /// <param name="scripts">Scripts to apply, in order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the apply, including any per-file failures.</returns>
    Task<SchemaApplyResult> ApplySchemaAsync(
        DbConnection connection,
        IReadOnlyList<SchemaScript> scripts,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the objects the harness can see, used for shape guarding and coverage.</summary>
    Task<IReadOnlyList<SchemaObject>> ListObjectsAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Empties every user table and resets identity counters, in an order that satisfies
    /// foreign keys.
    /// </summary>
    Task CleanAsync(DbConnection connection, CancellationToken cancellationToken = default);

    /// <summary>Runs a single statement and returns the number of rows it affected.</summary>
    Task<int> ExecuteAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a single statement with parameters and returns the number of rows it affected.</summary>
    Task<int> ExecuteAsync(
        DbConnection connection,
        string sql,
        IReadOnlyList<DbParameterValue> parameters,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a query and maps the first row to <typeparamref name="T"/>.</summary>
    Task<T?> QuerySingleAsync<T>(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a query with parameters and maps the first row to <typeparamref name="T"/>.</summary>
    Task<T?> QuerySingleAsync<T>(
        DbConnection connection,
        string sql,
        IReadOnlyList<DbParameterValue> parameters,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A named parameter value, in a provider-neutral form.
/// </summary>
/// <param name="Name">The parameter name, without the provider's prefix character.</param>
/// <param name="Value">The value, or null for SQL NULL.</param>
/// <remarks>
/// Each engine turns this into its own parameter type, so a caller never has to reference
/// <c>SqlParameter</c>, <c>NpgsqlParameter</c> or <c>MySqlParameter</c>.
/// </remarks>
public sealed record DbParameterValue(string Name, object? Value);

// probe
