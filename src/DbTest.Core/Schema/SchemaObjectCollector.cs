namespace DbTest.Core.Schema;

/// <summary>
/// Assembles <see cref="SchemaObject"/> values from catalog rows that arrive one column (or
/// one routine parameter) at a time.
/// </summary>
/// <remarks>
/// Every engine's catalog query returns one row per column, joined to its owning object, in
/// ordinal order. This groups them. The first row of an object fixes its kind; rows with a
/// null column only register the object.
/// </remarks>
public sealed class SchemaObjectCollector
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Entry> _order = [];

    /// <summary>Registers an object, and one of its columns when <paramref name="column"/> is not empty.</summary>
    /// <param name="schemaName">The schema (or database) the object lives in.</param>
    /// <param name="name">The object name.</param>
    /// <param name="kind">The object kind; ignored when the object is already registered.</param>
    /// <param name="column">A column or parameter name, or null to register the object alone.</param>
    public void Add(string schemaName, string name, SchemaObjectKind kind, string? column)
    {
        var key = string.IsNullOrEmpty(schemaName) ? name : $"{schemaName}.{name}";
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry(schemaName, name, kind);
            _entries[key] = entry;
            _order.Add(entry);
        }

        if (!string.IsNullOrEmpty(column))
        {
            entry.Columns.Add(column);
        }
    }

    /// <summary>The collected objects, in the order they were first seen.</summary>
    public IReadOnlyList<SchemaObject> Build()
    {
        return _order.Select(e => new SchemaObject(e.SchemaName, e.Name, e.Kind, e.Columns.ToArray())).ToList();
    }

    private sealed class Entry(string schemaName, string name, SchemaObjectKind kind)
    {
        public string SchemaName { get; } = schemaName;

        public string Name { get; } = name;

        public SchemaObjectKind Kind { get; } = kind;

        public List<string> Columns { get; } = [];
    }
}
