using DbTest.Core;
using DbTest.Core.Engine;

namespace DbTest.Integration.Tests;

/// <summary>
/// The same test body, run against every engine. This is the property that makes the tool
/// worth having: the assertions are written once and hold on SQL Server, PostgreSQL and
/// MySQL, because no engine-specific detail leaks into the test.
/// </summary>
public abstract class HarnessBehaviourTestsBase : EngineTestBase
{
    /// <summary>The customer email used by the seeded rows.</summary>
    protected const string CustomerEmail = "ada@example.test";

    [TestMethod]
    public async Task Container_Should_Start_And_Accept_Connections()
    {
        await InitializeAsync();

        var result = await Harness.QuerySingleAsync<int>("SELECT 1");

        Assert.AreEqual(1, result, "The harness should be able to run a trivial query.");
    }

    [TestMethod]
    public async Task Schema_Should_Be_Applied_With_Its_Tables_And_Routines()
    {
        await InitializeAsync();

        var objects = await Harness.ExecuteOnConnectionAsync(async connection =>
            await Harness.Engine.ListObjectsAsync(connection));

        var tableNames = objects
            .Where(o => o.Kind == DbTest.Core.Schema.SchemaObjectKind.Table)
            .Select(o => o.Name)
            .ToList();

        CollectionAssert.Contains(tableNames, "customers", "The customers table should exist.");
        CollectionAssert.Contains(tableNames, "orders", "The orders table should exist.");

        // The routine proves the engine-specific splitting worked: a dollar-quoted body on
        // PostgreSQL, a DELIMITER body on MySQL, a GO-separated batch on SQL Server.
        var routineNames = objects
            .Where(o => o.Kind == DbTest.Core.Schema.SchemaObjectKind.Routine)
            .Select(o => o.Name)
            .ToList();

        CollectionAssert.Contains(
            routineNames,
            "customer_order_total",
            $"The routine should exist. Found: {string.Join(", ", routineNames)}");
    }

    [TestMethod]
    public async Task Seeded_Rows_Should_Be_Readable_And_Countable()
    {
        await InitializeAsync();
        await SeedAsync();

        var count = await Harness.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM customers WHERE email = @email",
            [new DbParameterValue("@email", CustomerEmail)]);

        Assert.AreEqual(1, count, "The seeded customer should be visible.");
    }

    [TestMethod]
    public async Task Clean_Should_Empty_Tables_And_Reset_Identity()
    {
        await InitializeAsync();
        await SeedAsync();

        await Harness.CleanAsync();

        var count = await Harness.QuerySingleAsync<int>("SELECT COUNT(*) FROM customers");
        Assert.AreEqual(0, count, "Clean should empty the customers table.");

        // The identity counter must restart, otherwise a test that seeds a fixed id would
        // collide with rows a previous test left behind.
        await Harness.ExecuteAsync(
            $"INSERT INTO customers (email, display_name) VALUES ('{Guid.NewGuid():N}@example.test', 'Probe')");

        var maxId = await Harness.QuerySingleAsync<int>("SELECT MAX(id) FROM customers");
        Assert.AreEqual(1, maxId, "Identity should restart at 1 after a clean.");
    }

    [TestMethod]
    public async Task Foreign_Key_Should_Be_Enforced()
    {
        await InitializeAsync();
        await Harness.CleanAsync();

        await Assert.ThrowsAsync<Exception>(async () =>
            await Harness.ExecuteAsync(
                "INSERT INTO orders (customer_id, total_cents) VALUES (999999, 100)"),
            "A row referencing a missing customer must be rejected.");
    }

    [TestMethod]
    public async Task Shape_Guard_Should_Report_An_Object_Left_Reshaped()
    {
        await InitializeAsync();
        await Harness.CleanAsync();

        // Reshape a shared table: add a column, then leave it there. The guard must notice.
        await Harness.ExecuteAsync(AddProbeColumnSql);

        var leaks = await Harness.CheckShapeAsync("ShapeGuardProbeTests");

        Assert.IsTrue(
            leaks.Any(l => l.ObjectName.EndsWith("customers", StringComparison.OrdinalIgnoreCase)),
            $"The guard should report the reshaped customers table. Reported: {string.Join(", ", leaks.Select(l => l.ObjectName))}");

        // Put the schema back so the rest of the run is unaffected.
        await Harness.ExecuteAsync(DropProbeColumnSql);
    }

    /// <summary>
    /// Adds the probe column. The DDL is dialect-specific, the assertion is not: that split
    /// is the honest shape of cross-engine work, and it is why the harness keeps everything
    /// except raw DDL engine-agnostic.
    /// </summary>
    protected abstract string AddProbeColumnSql { get; }

    /// <summary>Removes the probe column added by <see cref="AddProbeColumnSql"/>.</summary>
    protected abstract string DropProbeColumnSql { get; }

    /// <summary>Seeds the reference rows the assertions read.</summary>
    protected async Task SeedAsync()
    {
        await Harness.CleanAsync();

        await Harness.ExecuteAsync(
            "INSERT INTO customers (email, display_name) VALUES (@email, @name)",
            [
                new DbParameterValue("@email", CustomerEmail),
                new DbParameterValue("@name", "Ada Lovelace"),
            ]);

        await Harness.ExecuteAsync(
            "INSERT INTO orders (customer_id, total_cents) " +
            "SELECT id, 2500 FROM customers WHERE email = @email",
            [new DbParameterValue("@email", CustomerEmail)]);
    }
}
