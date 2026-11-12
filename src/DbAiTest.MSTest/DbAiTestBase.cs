using System.Diagnostics;

using DbAiTest.Core;
using DbAiTest.Core.Engine;
using DbAiTest.Core.Schema;

namespace DbAiTest.MSTest;

/// <summary>
/// Base class for MSTest classes that run against a database engine.
/// </summary>
/// <remarks>
/// <para>
/// Write the tests once in an abstract class that derives from this one, then add one
/// <c>[TestClass]</c> per engine that overrides <see cref="CreateEngine"/> (and usually
/// <see cref="SchemaRoot"/>). The assertions never name an engine.
/// </para>
/// <para>
/// Around every test the base class:
/// </para>
/// <list type="bullet">
///   <item>hands the test the run's shared <see cref="Harness"/> for the engine, starting the
///   container on first use (<see cref="DbAiTestRun"/>);</item>
///   <item>after the test — and after any <c>[TestCleanup]</c> of the derived class — runs the
///   shape guard and fails the test that left a shared object reshaped, naming the object and
///   the change;</item>
///   <item>records the test's duration when <c>DBAITEST_TIMING=1</c>.</item>
/// </list>
/// <para>
/// Tests share one container, so the suite must run sequentially: add
/// <c>[assembly: DoNotParallelize]</c> to the test project.
/// </para>
/// </remarks>
public abstract class DbAiTestBase
{
    private Stopwatch? _stopwatch;

    /// <summary>The MSTest context, set by the framework.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>The shared harness for the engine under test. Available from <c>[TestInitialize]</c> on.</summary>
    protected DbAiTestHarness Harness { get; private set; } = null!;

    /// <summary>
    /// The schema directory for the engine, or null for none. A relative path is resolved
    /// against the test binaries' directory.
    /// </summary>
    protected virtual string? SchemaRoot => null;

    /// <summary>
    /// The schema source for the engine, or null to use <see cref="SchemaRoot"/>. Use it for a
    /// schema that is not a directory of SQL files, such as a SQL Server <c>.dacpac</c>. A
    /// relative path inside a source resolves against the current directory: pass
    /// <c>Path.Join(AppContext.BaseDirectory, …)</c> for files copied next to the tests.
    /// Setting it together with <see cref="SchemaRoot"/> or <see cref="SchemaLoadOptions"/>
    /// is an error.
    /// </summary>
    protected virtual SchemaSource? SchemaSource => null;

    /// <summary>Container options, or null for the engine defaults.</summary>
    protected virtual DbContainerOptions? ContainerOptions => null;

    /// <summary>Schema load options, or null for the defaults.</summary>
    protected virtual SchemaLoadOptions? SchemaLoadOptions => null;

    /// <summary>
    /// Whether a test that leaves a shared object reshaped fails. True by default: the failure
    /// lands on the culprit, which is the point. When false, the leak is only written to the
    /// test output.
    /// </summary>
    protected virtual bool FailOnShapeLeak => true;

    /// <summary>Creates the engine under test. Called before every test; only the first call per run starts a container.</summary>
    protected abstract DbEngine CreateEngine();

    /// <summary>Attaches the shared harness and starts the test's timer.</summary>
    [TestInitialize]
    public async Task InitializeDbAiTestAsync()
    {
        var schema = ResolveSchema(SchemaSource, SchemaRoot, SchemaLoadOptions);

        Harness = schema is null
            ? await DbAiTestRun.GetHarnessAsync(
                CreateEngine(),
                SchemaRoot,
                ContainerOptions,
                SchemaLoadOptions,
                TestContext.CancellationToken).ConfigureAwait(false)
            : await DbAiTestRun.GetHarnessAsync(
                CreateEngine(),
                schema,
                ContainerOptions,
                TestContext.CancellationToken).ConfigureAwait(false);

        _stopwatch = Stopwatch.StartNew();
    }

    /// <summary>
    /// Picks the schema configuration: the source when one is given, otherwise null so the
    /// directory settings apply. Mixing the two is refused rather than silently ignored.
    /// </summary>
    /// <exception cref="InvalidOperationException">A source is combined with directory settings.</exception>
    internal static SchemaSource? ResolveSchema(SchemaSource? schema, string? schemaRoot, SchemaLoadOptions? loadOptions)
    {
        if (schema is null)
        {
            return null;
        }

        if (schemaRoot is not null || loadOptions is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(SchemaSource)} cannot be combined with {nameof(SchemaRoot)} or {nameof(SchemaLoadOptions)}: "
                + $"a schema source carries its own location and options. Override only {nameof(SchemaSource)}, "
                + $"or use {nameof(DirectorySchemaSource)} for a directory.");
        }

        return schema;
    }

    /// <summary>Records the test's duration and runs the shape guard against it.</summary>
    [TestCleanup]
    public async Task CleanupDbAiTestAsync()
    {
        if (_stopwatch is null)
        {
            // The harness never started; the initialize failure is the one worth reporting.
            return;
        }

        _stopwatch.Stop();
        var testName = $"{GetType().Name}.{TestContext.TestName}";
        DbAiTestRun.Diagnostics?.Record(testName, _stopwatch.Elapsed);

        var leaks = await Harness.CheckShapeAsync(testName, TestContext.CancellationToken).ConfigureAwait(false);
        if (leaks.Count == 0)
        {
            return;
        }

        var report = string.Join(Environment.NewLine, leaks);
        TestContext.WriteLine(report);

        if (FailOnShapeLeak)
        {
            Assert.Fail(
                "The test left a shared database object reshaped. Restore it before the test ends "
                + "(the shape guard compares against the schema as applied):"
                + Environment.NewLine + report);
        }
    }
}
