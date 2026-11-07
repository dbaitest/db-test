namespace DbTest.Core.Schema;

/// <summary>The kind of a database object, normalized across engines.</summary>
public enum SchemaObjectKind
{
    /// <summary>A base table.</summary>
    Table,

    /// <summary>A view.</summary>
    View,

    /// <summary>A stored procedure or function.</summary>
    Routine,
}

/// <summary>
/// A database object as the harness sees it: identity, kind, and the columns it exposes.
/// </summary>
/// <param name="SchemaName">The schema (or database) the object lives in.</param>
/// <param name="Name">The object name.</param>
/// <param name="Kind">What kind of object it is.</param>
/// <param name="Columns">Column names, in ordinal order.</param>
public sealed record SchemaObject(
    string SchemaName,
    string Name,
    SchemaObjectKind Kind,
    IReadOnlyList<string> Columns)
{
    /// <summary>Fully qualified name, used as the stable key in reports and guards.</summary>
    public string QualifiedName => string.IsNullOrEmpty(SchemaName) ? Name : $"{SchemaName}.{Name}";

    /// <summary>
    /// A fingerprint of the object's shape: kind plus ordered column names.
    /// </summary>
    /// <remarks>
    /// Two objects with the same fingerprint are interchangeable in shape. The guard uses
    /// this to detect a test that dropped an object, re-typed it, or changed its columns.
    /// </remarks>
    public string ShapeFingerprint => $"{Kind}:{string.Join(',', Columns)}";

    /// <inheritdoc />
    public override string ToString() => QualifiedName;
}
