namespace DbAiTest.Core.Schema;

/// <summary>
/// A schema kept as a directory of SQL files, applied in path order by the engine. This is
/// what <c>schemaRoot</c> does; see <see cref="SchemaLoader"/> for discovery, ordering and
/// exclusion.
/// </summary>
public sealed class DirectorySchemaSource : SchemaSource
{
    private readonly SchemaLoadOptions _options;

    /// <summary>Creates the source.</summary>
    /// <param name="rootDirectory">
    /// Directory holding the schema files. A relative path is resolved against the current
    /// directory now, exactly as <see cref="SchemaLoader.Load"/> would.
    /// </param>
    /// <param name="options">Load options, or null for the defaults. The lists are copied.</param>
    public DirectorySchemaSource(string rootDirectory, SchemaLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        RootDirectory = Path.GetFullPath(rootDirectory);

        // Copied so that changing the caller's lists later cannot make Key disagree with what
        // is applied.
        _options = new SchemaLoadOptions
        {
            ExcludedDirectories = [.. options?.ExcludedDirectories ?? []],
            ExcludedFileNamePatterns = [.. options?.ExcludedFileNamePatterns ?? []],
            OrderedDirectories = [.. options?.OrderedDirectories ?? []],
        };

        Key = string.Join(
            '\u001f',
            "dir",
            RootDirectory,
            string.Join(',', _options.ExcludedDirectories),
            string.Join(',', _options.ExcludedFileNamePatterns),
            string.Join(',', _options.OrderedDirectories));
    }

    /// <summary>The absolute schema directory.</summary>
    public string RootDirectory { get; }

    /// <inheritdoc />
    public override string Key { get; }

    /// <inheritdoc />
    /// <exception cref="DirectoryNotFoundException">The schema directory does not exist.</exception>
    public override async Task<SchemaApplyResult> ApplyAsync(
        SchemaApplyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var load = SchemaLoader.Load(RootDirectory, _options);

        await using var connection = context.Engine.CreateConnection(context.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await context.Engine.ApplySchemaAsync(connection, load.Scripts, cancellationToken)
            .ConfigureAwait(false);
    }
}
