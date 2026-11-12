namespace DbAiTest.Core.Schema;

/// <summary>
/// A single SQL file that makes up part of a database schema, as it is discovered on disk.
/// </summary>
/// <param name="Path">Absolute path of the file on disk.</param>
/// <param name="RelativePath">Path relative to the schema root, using forward slashes. Stable across machines, so it is safe to print and to key reports by.</param>
/// <param name="Content">Full text of the file.</param>
public sealed record SchemaScript(string Path, string RelativePath, string Content)
{
    /// <summary>The directory the file lives in, relative to the schema root. Used to group objects by folder.</summary>
    public string RelativeDirectory
    {
        get
        {
            var slash = RelativePath.LastIndexOf('/');
            return slash < 0 ? string.Empty : RelativePath[..slash];
        }
    }
}
