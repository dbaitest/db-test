using System.Diagnostics;

using DbTest.Core;
using DbTest.Core.Engine;
using DbTest.Core.Schema;

namespace DbTest.MSTest;

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
///   container on first use (<see cref="DbTestRun"/>);</item>
///   <item>after the test — and after any <c>[TestCleanup]</c> of the derived class — runs the
///   shape guard and fails the test that left a shared object reshaped, naming the object and
///   the change;</item>
///   <item>records the test's duration when <c>DBTEST_TIMING=1</c>.</item>
/// </list>
/// <para>
/// Tests share one container, so the suite must run sequentially: add
/// <c>[assembly: DoNotParallelize]</c> to the test project.
/// </para>
/// </remarks>
public abstract class DbTestBase
{
    private Stopwatch? _stopwatch;

    /// <summary>The MSTest context, set by the framework.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>The shared harness for the engine under test. Available from <c>[TestInitialize]</c> on.</summary>
    protected DbTestHarness Harness { get; private set; } = null!;

    /// <summary>
    /// The schema directory for the engine, or null for none. A relative path is resolved
    /// against the test binaries' directory.
    /// </summary>
    protected virtual string? SchemaRoot => null;

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
    public async Task InitializeDbTestAsync()
    {
        Harness = await DbTestRun.GetHarnessAsync(
            CreateEngine(),
            SchemaRoot,
            ContainerOptions,
            SchemaLoadOptions,
            TestContext.CancellationToken).ConfigureAwait(false);

        _stopwatch = Stopwatch.StartNew();
    }

    /// <summary>Records the test's duration and runs the shape guard against it.</summary>
    [TestCleanup]
    public async Task CleanupDbTestAsync()
    {
        if (_stopwatch is null)
        {
            // The harness never started; the initialize failure is the one worth reporting.
            return;
        }

        _stopwatch.Stop();
        var testName = $"{GetType().Name}.{TestContext.TestName}";
        DbTestRun.Diagnostics?.Record(testName, _stopwatch.Elapsed);

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
