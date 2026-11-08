using DbTest.Core.Engine;
using DbTest.Core.Schema;
using DbTest.MSTest;

namespace DbTest.Integration.Tests;

/// <summary>
/// The same test body, run against every engine. This is the property that makes the tool
/// worth having: the assertions are written once and hold on SQL Server, PostgreSQL and
/// MySQL, because no engine-specific detail leaks into the test.
/// </summary>
/// <remarks>
/// The suite is built on DbTest.MSTest, exactly as a consumer would use it: the base class
/// supplies the shared harness, and runs the shape guard after every test.
/// </remarks>
public abstract class HarnessBehaviourTestsBase : DbTestBase
{
    /// <summary>The customer email used by the seeded rows.</summary>
    protected const string CustomerEmail = "ada@example.test";

    /// <summary>The engine's directory under the copied Schema folder.</summary>
    protected abstract string SchemaDirectory { get; }

    /// <inheritdoc />
    protected override string SchemaRoot => Path.Combine("Schema", SchemaDirectory);

    /// <summary>
    /// Adds the probe column. The DDL is dialect-specific, the assertion is not: that split
    /// is the honest shape of cross-engine work, and it is why the harness keeps everything
    /// except raw DDL engine-agnostic.
    /// </summary>
    protected abstract string AddProbeColumnSql { get; }

    /// <summary>Removes the probe column added by <see cref="AddProbeColumnSql"/>.</summary>
    protected abstract string DropProbeColumnSql { get; }

    [TestMethod]
    public async Task Container_Should_Start_And_Accept_Connections()
    {
        var result = await Harness.QueryScalarAsync<int>("SELECT 1");

        Assert.AreEqual(1, result, "The harness should be able to run a trivial query.");
    }

    [TestMethod]
    public async Task Schema_Should_Be_Applied_With_Its_Tables_And_Routines()
    {
        var objects = await Harness.ListObjectsAsync();

        var tableNames = objects.Where(o => o.Kind == SchemaObjectKind.Table).Select(o => o.Name).ToList();

        CollectionAssert.Contains(tableNames, "customers", "The customers table should exist.");
        CollectionAssert.Contains(tableNames, "orders", "The orders table should exist.");

        // The routine proves the engine-specific splitting worked: a dollar-quoted body on
        // PostgreSQL, a DELIMITER body on MySQL, a GO-separated batch on SQL Server.
        var routineNames = objects.Where(o => o.Kind == SchemaObjectKind.Routine).Select(o => o.Name).ToList();

        CollectionAssert.Contains(
            routineNames,
            "customer_order_total",
            $"The routine should exist. Found: {string.Join(", ", routineNames)}");
    }

    [TestMethod]
    public async Task Seeded_Rows_Should_Be_Readable_And_Countable()
    {
        await SeedAsync();

        var count = await Harness.QueryScalarAsync<int>(
            "SELECT COUNT(*) FROM customers WHERE email = @email",
            [new DbParameterValue("@email", CustomerEmail)]);

        Assert.AreEqual(1, count, "The seeded customer should be visible.");
    }

    [TestMethod]
    public async Task Clean_Should_Empty_Tables_And_Reset_Identity()
    {
        await SeedAsync();

        await Harness.CleanAsync();

        var count = await Harness.QueryScalarAsync<int>("SELECT COUNT(*) FROM customers");
        Assert.AreEqual(0, count, "Clean should empty the customers table.");

        // The identity counter must restart, otherwise a test that seeds a fixed id would
        // collide with rows a previous test left behind.
        await Harness.ExecuteAsync(
            "INSERT INTO customers (email, display_name) VALUES (@email, 'Probe')",
            [new DbParameterValue("@email", $"{Guid.NewGuid():N}@example.test")]);

        var maxId = await Harness.QueryScalarAsync<int>("SELECT MAX(id) FROM customers");
        Assert.AreEqual(1, maxId, "Identity should restart at 1 after a clean.");
    }

    [TestMethod]
    public async Task Clean_Should_Reset_Identity_Of_A_Table_That_Never_Held_A_Row()
    {
        // audit_entries is never seeded. SQL Server treats a reseed of such a table differently
        // from a reseed of a table that has held rows; the first id must still be 1.
        await Harness.CleanAsync();

        await Harness.ExecuteAsync("INSERT INTO audit_entries (note) VALUES ('first')");

        var id = await Harness.QueryScalarAsync<int>("SELECT MAX(id) FROM audit_entries");
        Assert.AreEqual(1, id, "The first row of a never-used table should get id 1 after a clean.");

        await Harness.CleanAsync();
    }

    [TestMethod]
    public async Task Scalar_Should_Convert_Null_And_Driver_Specific_Types()
    {
        await Harness.CleanAsync();

        var none = await Harness.QueryScalarAsync<int?>("SELECT MAX(id) FROM customers");
        Assert.IsNull(none, "MAX over an empty table is SQL NULL, which a nullable target receives as null.");

        await SeedAsync();

        // COUNT(*) is int on SQL Server and bigint on PostgreSQL and MySQL; both targets work.
        var asInt = await Harness.QueryScalarAsync<int?>("SELECT COUNT(*) FROM customers");
        var asLong = await Harness.QueryScalarAsync<long>("SELECT COUNT(*) FROM customers");

        Assert.AreEqual(1, asInt);
        Assert.AreEqual(1L, asLong);
    }

    [TestMethod]
    public async Task Foreign_Key_Should_Be_Enforced()
    {
        await Harness.CleanAsync();

        await Assert.ThrowsAsync<Exception>(
            async () => await Harness.ExecuteAsync("INSERT INTO orders (customer_id, total_cents) VALUES (999999, 100)"),
            "A row referencing a missing customer must be rejected.");
    }

    [TestMethod]
    public async Task Shape_Guard_Should_Report_An_Object_Left_Reshaped()
    {
        // Reshape a shared table: add a column. The guard must notice.
        await Harness.ExecuteAsync(AddProbeColumnSql);

        try
        {
            var leaks = await Harness.CheckShapeAsync("ShapeGuardProbe");

            var leak = leaks.SingleOrDefault(l => l.ObjectName.EndsWith("customers", StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(
                leak,
                $"The guard should report the reshaped customers table. Reported: {string.Join(", ", leaks)}");
            Assert.AreEqual("ShapeGuardProbe", leak.Responsible);
            StringAssert.Contains(leak.Change, "dbtest_shape_probe");
        }
        finally
        {
            // Put the schema back, so the guard that runs after this test finds it intact.
            await Harness.ExecuteAsync(DropProbeColumnSql);
        }
    }

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
            "INSERT INTO orders (customer_id, total_cents) SELECT id, 2500 FROM customers WHERE email = @email",
            [new DbParameterValue("@email", CustomerEmail)]);
    }
}
