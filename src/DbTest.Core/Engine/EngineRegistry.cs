namespace DbTest.Core.Engine;

/// <summary>
/// Resolves an engine by name.
/// </summary>
/// <remarks>
/// The core assembly deliberately references no engine package, so the mapping from a name to
/// an implementation lives with the application that owns the engine packages. The test suite
/// and the CLI register theirs here.
/// </remarks>
public static class EngineRegistry
{
    private static readonly Dictionary<string, Func<DbEngine>> Factories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers an engine under a name. Later registrations replace earlier ones.</summary>
    public static void Register(string name, Func<DbEngine> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        Factories[name] = factory;
    }

    /// <summary>The names of every registered engine, sorted.</summary>
    public static IReadOnlyList<string> Names => Factories.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Creates the engine registered under <paramref name="name"/>.</summary>
    /// <exception cref="DbTestException">No engine is registered under that name.</exception>
    public static DbEngine Create(string name)
    {
        if (!Factories.TryGetValue(name, out var factory))
        {
            var known = Names.Count == 0 ? "(none registered)" : string.Join(", ", Names);
            throw new DbTestException($"Unknown engine '{name}'. Registered engines: {known}");
        }

        return factory();
    }
}
