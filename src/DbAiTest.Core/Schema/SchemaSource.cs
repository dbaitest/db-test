using DbAiTest.Core.Engine;

namespace DbAiTest.Core.Schema;

/// <summary>
/// Puts a schema into a freshly started, empty database.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DirectorySchemaSource"/> applies a directory of SQL files, which is what
/// <c>schemaRoot</c> has always done. Other sources deploy the schema some other way — a
/// SQL Server <c>.dacpac</c>, for example — and plug into the same harness, shape guard and
/// cleanup.
/// </para>
/// <para>
/// A source resolves a relative path against the current directory. To resolve one against
/// the test binaries' directory, pass <c>Path.Join(AppContext.BaseDirectory, …)</c>.
/// </para>
/// <para>
/// This is an abstract class rather than an interface for the same reason as
/// <see cref="DbEngine"/>: a new capability can arrive as a virtual member with a default,
/// without breaking a source written outside this repository.
/// </para>
/// </remarks>
public abstract class SchemaSource
{
    /// <summary>
    /// Identifies the schema this source produces. Two sources with equal keys must deploy the
    /// same schema: a test run shares one container between them.
    /// </summary>
    /// <remarks>
    /// Must be pure: no file-system or network access, and never throw. The key is computed
    /// before any container starts, so a missing file has to be reported by
    /// <see cref="Validate"/>, not here.
    /// </remarks>
    public abstract string Key { get; }

    /// <summary>
    /// Checks the configuration before a container is started, so a mistake (a missing file, an
    /// engine the source cannot deploy to) fails in milliseconds rather than after an image pull
    /// and a server boot. The default does nothing.
    /// </summary>
    /// <param name="engine">The engine the schema is about to be deployed to.</param>
    /// <exception cref="DbAiTestException">The source cannot deploy to this engine as configured.</exception>
    public virtual void Validate(DbEngine engine)
    {
    }

    /// <summary>
    /// Applies the schema to the database the context points at.
    /// </summary>
    /// <remarks>
    /// A schema error is returned as a failure in the result, not thrown, so the caller can
    /// report every broken object at once; <see cref="SchemaApplyResult.ThrowIfFailed"/> turns
    /// it into an exception.
    /// </remarks>
    /// <param name="context">The engine and the database to apply to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the apply, including any failures.</returns>
    public abstract Task<SchemaApplyResult> ApplyAsync(
        SchemaApplyContext context,
        CancellationToken cancellationToken = default);
}
