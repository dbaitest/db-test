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
/// object after the schema is applied and re-reads it after each test class, then names the
/// class that changed the shape.
/// </para>
/// <para>
/// Detection only. The guard reports what changed and who changed it; it does not repair,
/// because a repair that silently reverts a leak hides the leak from the next run.
/// </para>
/// </remarks>
public sealed class SchemaShapeGuard
{
    private readonly IDbEngine _engine;
    private readonly IReadOnlyDictionary<string, SchemaObject> _baseline;
    private readonly HashSet<string> _reported;
    private readonly List<SchemaShapeLeak> _leaks = [];

    /// <summary>Creates a guard over a baseline snapshot of the schema.</summary>
    /// <param name="engine">The engine to read the live schema through.</param>
    /// <param name="baseline">Objects as they were when the schema was applied.</param>
    public SchemaShapeGuard(IDbEngine engine, IReadOnlyDictionary<string, SchemaObject> baseline)
    {
        _engine = engine;
        _baseline = baseline;
        _reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Leaks detected so far.</summary>
    public IReadOnlyList<SchemaShapeLeak> Leaks => _leaks;

    /// <summary>
    /// Captures the current schema and reports every object whose shape differs from the baseline.
    /// </summary>
    /// <param name="connection">An open connection to the database under test.</param>
    /// <param name="responsibleClass">Name of the test class being blamed, usually the class that just ran.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The leaks found in this pass.</returns>
    public async Task<IReadOnlyList<SchemaShapeLeak>> CheckAsync(
        System.Data.Common.DbConnection connection,
        string responsibleClass,
        CancellationToken cancellationToken = default)
    {
        var current = await CaptureAsync(connection, cancellationToken).ConfigureAwait(false);
        var found = new List<SchemaShapeLeak>();

        foreach (var (name, baselineObject) in _baseline)
        {
            if (!current.TryGetValue(name, out var live))
            {
                found.Add(new SchemaShapeLeak(name, baselineObject.Kind, "dropped", responsibleClass));
                continue;
            }

            if (live.Kind != baselineObject.Kind)
            {
                found.Add(new SchemaShapeLeak(
                    name,
                    baselineObject.Kind,
                    $"re-typed ({baselineObject.Kind} -> {live.Kind})",
                    responsibleClass));
                continue;
            }

            if (!live.Columns.SequenceEqual(baselineObject.Columns, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(new SchemaShapeLeak(
                    name,
                    baselineObject.Kind,
                    $"columns changed ({Describe(baselineObject.Columns)} -> {Describe(live.Columns)})",
                    responsibleClass));
            }
        }

        foreach (var leak in found)
        {
            // Report each object once. The first class to change it is the culprit; later
            // classes only inherited the damage and reporting them would dilute the signal.
            if (_reported.Add(leak.ObjectName))
            {
                _leaks.Add(leak);
            }
        }

        return found;
    }

    /// <summary>Captures the live shape of every object the engine can see.</summary>
    public async Task<IReadOnlyDictionary<string, SchemaObject>> CaptureAsync(
        System.Data.Common.DbConnection connection,
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
/// <param name="ResponsibleClass">The test class blamed for the change.</param>
public sealed record SchemaShapeLeak(
    string ObjectName,
    SchemaObjectKind Kind,
    string Change,
    string ResponsibleClass)
{
    /// <inheritdoc />
    public override string ToString() => $"[ShapeGuard] {ResponsibleClass} left {ObjectName} {Change}";
}
