using System.Data.Common;

using DbTest.Core.Engine;
using DbTest.Core.Schema;

namespace DbTest.Core.Diagnostics;

/// <summary>
/// Detects tests that change the shape of a shared database object and leave it changed.
/// </summary>
/// <remarks>
/// <para>
/// This is the single most valuable thing in an integration suite that shares one container:
/// without it, one test that drops a table leaves the next test failing on a missing object,
/// and the failure points at the victim rather than the culprit. The guard snapshots every
/// object after the schema is applied and re-reads it after each test (DbTest.MSTest does this
/// automatically), then names the test that changed the shape.
/// </para>
/// <para>
/// Detection only. The guard reports what changed and who changed it; it does not repair,
/// because a repair that silently reverts a leak hides the leak from the next run.
/// </para>
/// </remarks>
public sealed class SchemaShapeGuard
{
    private readonly DbEngine _engine;
    private readonly IReadOnlyDictionary<string, SchemaObject> _baseline;
    private readonly HashSet<string> _outstanding;
    private readonly List<SchemaShapeLeak> _leaks = [];

    /// <summary>Creates a guard over a baseline snapshot of the schema.</summary>
    /// <param name="engine">The engine to read the live schema through.</param>
    /// <param name="baseline">Objects as they were when the schema was applied.</param>
    public SchemaShapeGuard(DbEngine engine, IReadOnlyDictionary<string, SchemaObject> baseline)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(baseline);

        _engine = engine;
        _baseline = baseline;
        _outstanding = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every leak detected so far, in the order it was detected.</summary>
    public IReadOnlyList<SchemaShapeLeak> Leaks => _leaks;

    /// <summary>
    /// Captures the current schema and reports every object whose shape newly differs from the
    /// baseline.
    /// </summary>
    /// <remarks>
    /// Each damaged object is reported once, against the first caller that saw it damaged: that
    /// is the culprit, and later callers only inherited the damage. An object that is put back
    /// into its baseline shape is forgotten, so a later test that breaks it again is blamed again.
    /// </remarks>
    /// <param name="connection">An open connection to the database under test.</param>
    /// <param name="responsible">The test (or test class) being blamed, usually the one that just ran.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The leaks first detected in this pass.</returns>
    public async Task<IReadOnlyList<SchemaShapeLeak>> CheckAsync(
        DbConnection connection,
        string responsible,
        CancellationToken cancellationToken = default)
    {
        var current = await CaptureAsync(connection, cancellationToken).ConfigureAwait(false);
        var found = new List<SchemaShapeLeak>();

        foreach (var (name, baselineObject) in _baseline)
        {
            if (!current.TryGetValue(name, out var live))
            {
                found.Add(new SchemaShapeLeak(name, baselineObject.Kind, "dropped", responsible));
                continue;
            }

            if (live.Kind != baselineObject.Kind)
            {
                found.Add(new SchemaShapeLeak(
                    name,
                    baselineObject.Kind,
                    $"re-typed ({baselineObject.Kind} -> {live.Kind})",
                    responsible));
                continue;
            }

            if (!live.Columns.SequenceEqual(baselineObject.Columns, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(new SchemaShapeLeak(
                    name,
                    baselineObject.Kind,
                    $"columns changed ({Describe(baselineObject.Columns)}) -> ({Describe(live.Columns)})",
                    responsible));
            }
        }

        // Forget objects that were repaired, so breaking them again is reported again.
        _outstanding.RemoveWhere(name => found.All(l => !string.Equals(l.ObjectName, name, StringComparison.OrdinalIgnoreCase)));

        var fresh = found.Where(leak => _outstanding.Add(leak.ObjectName)).ToList();
        _leaks.AddRange(fresh);
        return fresh;
    }

    /// <summary>Captures the live shape of every object the engine can see.</summary>
    public async Task<IReadOnlyDictionary<string, SchemaObject>> CaptureAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        var objects = await _engine.ListObjectsAsync(connection, cancellationToken).ConfigureAwait(false);
        return objects.ToDictionary(o => o.QualifiedName, StringComparer.OrdinalIgnoreCase);
    }

    private static string Describe(IReadOnlyList<string> columns)
    {
        return columns.Count == 0 ? "(none)" : string.Join(',', columns);
    }
}

/// <summary>A shared object whose shape was changed and left changed.</summary>
/// <param name="ObjectName">Qualified name of the object.</param>
/// <param name="Kind">What kind of object it was.</param>
/// <param name="Change">What changed, in words.</param>
/// <param name="Responsible">The test (or test class) blamed for the change.</param>
public sealed record SchemaShapeLeak(
    string ObjectName,
    SchemaObjectKind Kind,
    string Change,
    string Responsible)
{
    /// <inheritdoc />
    public override string ToString() => $"[ShapeGuard] {Responsible} left {ObjectName} {Change}";
}
