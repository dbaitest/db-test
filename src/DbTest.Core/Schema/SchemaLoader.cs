using System.Text.RegularExpressions;

namespace DbTest.Core.Schema;

/// <summary>
/// Discovers SQL files under a schema root and turns them into an ordered list of
/// <see cref="SchemaScript"/> values.
/// </summary>
/// <remarks>
/// <para>
/// Ordering matters: the schema is applied file by file, so a file that references an
/// object created by an earlier file must come later. Files are ordered by their relative
/// path (ordinal, case-insensitive), so <c>001_tables.sql</c> runs before
/// <c>002_procedures.sql</c> and <c>procedures/</c> runs before <c>tables/</c> unless
/// <see cref="SchemaLoadOptions.OrderedDirectories"/> says otherwise. Name numbered files with
/// leading zeros: <c>10_x.sql</c> sorts before <c>2_x.sql</c>.
/// </para>
/// <para>
/// Exclusion is by convention, and every rule is overridable. Excluding a file by name
/// silently is the failure mode this loader is designed to avoid: an excluded file is
/// reported by <see cref="SchemaLoadResult.Excluded"/> so a caller can log or fail on it.
/// </para>
/// </remarks>
public static class SchemaLoader
{
    /// <summary>Extensions treated as SQL source, in the order they are probed.</summary>
    private static readonly string[] SqlExtensions = [".sql", ".psql"];

    /// <summary>A file-name pattern that takes longer than this is a broken pattern, not a slow one.</summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Loads the schema rooted at <paramref name="rootDirectory"/>.</summary>
    /// <param name="rootDirectory">Directory that holds the schema files.</param>
    /// <param name="options">Load options. Defaults are used when omitted.</param>
    /// <exception cref="DirectoryNotFoundException">The root directory does not exist.</exception>
    public static SchemaLoadResult Load(string rootDirectory, SchemaLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        options ??= new SchemaLoadOptions();

        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Schema root directory not found: {root}");
        }

        var excluded = new List<SchemaExclusion>();
        var scripts = new List<SchemaScript>();

        foreach (var file in EnumerateFiles(root, options))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            if (IsExcludedByDirectory(relative, options))
            {
                excluded.Add(new SchemaExclusion(relative, "directory-excluded"));
                continue;
            }

            if (IsExcludedByFileName(relative, options))
            {
                excluded.Add(new SchemaExclusion(relative, "filename-excluded"));
                continue;
            }

            var content = File.ReadAllText(file);
            scripts.Add(new SchemaScript(file, relative, content));
        }

        var ordered = Order(scripts, options);

        return new SchemaLoadResult(ordered, excluded);
    }

    private static IEnumerable<string> EnumerateFiles(string root, SchemaLoadOptions options)
    {
        var enumerable = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);

        foreach (var file in enumerable)
        {
            var extension = Path.GetExtension(file);
            if (!SqlExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    private static bool IsExcludedByDirectory(string relativePath, SchemaLoadOptions options)
    {
        if (options.ExcludedDirectories.Count == 0)
        {
            return false;
        }

        var segments = relativePath.Split('/');
        return segments.Take(segments.Length - 1)
            .Any(segment => options.ExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsExcludedByFileName(string relativePath, SchemaLoadOptions options)
    {
        var name = Path.GetFileName(relativePath);
        return options.ExcludedFileNamePatterns.Any(
            pattern => Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout));
    }

    private static List<SchemaScript> Order(List<SchemaScript> scripts, SchemaLoadOptions options)
    {
        var ordered = new List<SchemaScript>(scripts.Count);
        var remaining = scripts.OrderBy(s => s.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();

        // Explicit directory order first (each directory with everything below it), then
        // everything else.
        foreach (var directory in options.OrderedDirectories)
        {
            var prefix = directory.Replace('\\', '/').Trim('/') + "/";
            var inDirectory = remaining
                .Where(s => s.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            ordered.AddRange(inDirectory);
            remaining.RemoveAll(inDirectory.Contains);
        }

        ordered.AddRange(remaining);
        return ordered;
    }
}

/// <summary>Options controlling how a schema directory is discovered and ordered.</summary>
public sealed class SchemaLoadOptions
{
    /// <summary>Directory names (relative to the schema root) whose files are skipped entirely.</summary>
    public IList<string> ExcludedDirectories { get; init; } = [];

    /// <summary>Regular expressions matched against the file name; a match excludes the file.</summary>
    public IList<string> ExcludedFileNamePatterns { get; init; } = [];

    /// <summary>
    /// Directories (relative to the schema root) applied in this exact order, before any other
    /// file. A directory includes its subdirectories.
    /// </summary>
    public IList<string> OrderedDirectories { get; init; } = [];
}

/// <summary>The outcome of loading a schema directory.</summary>
/// <param name="Scripts">Scripts that will be applied, in apply order.</param>
/// <param name="Excluded">Files that were found and skipped, with the rule that skipped them.</param>
public sealed record SchemaLoadResult(IReadOnlyList<SchemaScript> Scripts, IReadOnlyList<SchemaExclusion> Excluded);

/// <summary>A file that was discovered but not applied.</summary>
/// <param name="RelativePath">Path relative to the schema root.</param>
/// <param name="Reason">Why the file was excluded.</param>
public sealed record SchemaExclusion(string RelativePath, string Reason);
