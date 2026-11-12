using DbAiTest.Core.Engine;

using Microsoft.Data.SqlClient;

using Sample.Database.Tests.Fixtures;
using Sample.Shop.Orders;

namespace Sample.Database.Tests;

/// <summary>
/// Drives the application's ADO.NET repository, <see cref="SqlOrderRepository"/>, against the
/// deployed database. The repository is the production class: only its connection factory
/// points at the container. Its SQL, parameter types and NULL handling are what is under test,
/// and none of that is reachable through a mocked connection.
/// </summary>
[TestClass]
public sealed class SqlOrderRepository_PlaceAndReadTests : ShopDatabaseTest
{
    private SqlOrderRepository Orders => new(Connections);

    /// <summary>
    /// Kills: a column read into the wrong property, or a Tax that is never read — the computed
    /// column (dbo.TaxFor over dbo.TaxRates) only has a value on a real server.
    /// </summary>
    [TestMethod]
    public async Task Placed_Order_Reads_Back_With_The_Computed_Tax()
    {
        var id = await Orders.PlaceAsync(new NewOrder("ada@example.test", "NL", 100.00m), TestContext.CancellationToken);

        var order = await Orders.GetAsync(id, TestContext.CancellationToken);

        Assert.AreEqual(new Order(1, 1, "NL", 100.00m, 21.00m), order);
        Assert.AreEqual(1, await Harness.QueryScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Customers WHERE Email = @email",
            [new DbParameterValue("@email", "ada@example.test")],
            TestContext.CancellationToken));
    }

    /// <summary>
    /// Kills: a NULL Tax read with an unguarded GetDecimal (throws) or defaulted to 0 (a wrong
    /// amount that looks right). A region without a rate has no tax, and the database says so.
    /// </summary>
    [TestMethod]
    public async Task Order_In_A_Region_Without_A_Rate_Has_No_Tax()
    {
        var id = await Orders.PlaceAsync(new NewOrder("ada@example.test", "FR", 50.00m), TestContext.CancellationToken);

        var order = await Orders.GetAsync(id, TestContext.CancellationToken);

        Assert.AreEqual(new Order(1, 1, "FR", 50.00m, null), order);
        Assert.IsNull(await Harness.QueryScalarAsync<decimal?>(
            "SELECT Tax FROM dbo.Orders WHERE Id = @id",
            [new DbParameterValue("@id", id)],
            TestContext.CancellationToken));
    }

    /// <summary>
    /// Kills: a not-found that throws or returns a default order. The table is not empty — the
    /// SQL fixture seeded three orders — so only a correct WHERE gives null.
    /// </summary>
    [TestMethod]
    public async Task Missing_Order_Reads_As_Null()
    {
        await ShopFixtures.LoadSqlAsync(Harness, "customers-with-orders.sql", TestContext.CancellationToken);

        Assert.IsNull(await Orders.GetAsync(99, TestContext.CancellationToken));
        Assert.AreEqual(new Order(1, 1, "NL", 100.00m, 21.00m), await Orders.GetAsync(1, TestContext.CancellationToken));
    }

    /// <summary>
    /// Kills: a batch that is not one transaction, or commits before its last order — the first
    /// order, and the customer dbo.PlaceOrder created for it, would survive the failure.
    /// </summary>
    [TestMethod]
    public async Task A_Failing_Batch_Leaves_Nothing_Behind()
    {
        // The second order breaks CK_Orders_Amount (Amount > 0).
        NewOrder[] batch = [new("ada@example.test", "NL", 10.00m), new("ada@example.test", "NL", 0.00m)];

        await Assert.ThrowsExactlyAsync<SqlException>(() => Orders.PlaceAllAsync(batch, TestContext.CancellationToken));

        // Asserted through the harness, not the repository: the repository is what is under test.
        Assert.AreEqual(0, await Harness.QueryScalarAsync<int>("SELECT COUNT(*) FROM dbo.Orders", TestContext.CancellationToken));
        Assert.AreEqual(0, await Harness.QueryScalarAsync<int>("SELECT COUNT(*) FROM dbo.Customers", TestContext.CancellationToken));
    }
}
