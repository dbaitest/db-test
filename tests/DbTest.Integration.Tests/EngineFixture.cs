using DbTest.Core;
using DbTest.Core.Engine;

namespace DbTest.Integration.Tests;

/// <summary>
/// Holds one harness per engine for the whole test run.
/// </summary>
/// <remarks>
/// A container per test class would multiply the startup cost for no benefit: the schema is
/// the same and the tests do not depend on a pristine database beyond what the harness resets
/// between classes. One container per engine keeps the run to a few minutes while still
/// giving each engine its own isolated database.
/// </remarks>
public static class EngineFixture
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, DbTestHarness> Harnesses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The schema directory copied next to the test binaries.</summary>
    public static string SchemaRoot { get; } = Path.Combine(AppContext.BaseDirectory, "Schema");

    /// <summary>Returns the harness for an engine, starting it on first use.</summary>
    public static async Task<DbTestHarness> GetAsync(string engineName, CancellationToken cancellationToken = default)
    {
        if (Harnesses.TryGetValue(engineName, out var existing))
        {
            return existing;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (Harnesses.TryGetValue(engineName, out existing))
            {
                return existing;
            }

            var engine = CreateEngine(engineName);
            var schemaDirectory = Path.Combine(SchemaRoot, engineName);

            var harness = await DbTestHarness.StartAsync(
                engine,
                Directory.Exists(schemaDirectory) ? schemaDirectory : null,
                new DbContainerOptions { Database = "dbtest" },
                cancellationToken: cancellationToken);

            Harnesses[engineName] = harness;
            return harness;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Stops every harness that was started.</summary>
    public static async Task DisposeAllAsync()
    {
        await Gate.WaitAsync();
        try
        {
            foreach (var harness in Harnesses.Values)
            {
                await harness.DisposeAsync();
            }

            Harnesses.Clear();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static IDbEngine CreateEngine(string engineName) => engineName switch
    {
        "mssql" => new DbTest.MsSql.MsSqlEngine(),
        "postgres" => new DbTest.PostgreSql.PostgreSqlEngine(),
        "mysql" => new DbTest.MySql.MySqlEngine(),
        _ => throw new ArgumentException($"Unknown engine '{engineName}'.", nameof(engineName)),
    };
}

/// <summary>
/// Base class for tests that run against one engine. The engine is chosen by the derived
/// class, and the harness is started lazily on first use.
/// </summary>
public abstract class EngineTestBase
{
    /// <summary>The name of the engine under test.</summary>
    protected abstract string EngineName { get; }

    /// <summary>The harness for the engine under test.</summary>
    protected DbTestHarness Harness { get; private set; } = null!;

    /// <summary>Starts the harness if it is not running yet.</summary>
    [TestInitialize]
    public async Task InitializeAsync()
    {
        Harness = await EngineFixture.GetAsync(EngineName);
    }
}
