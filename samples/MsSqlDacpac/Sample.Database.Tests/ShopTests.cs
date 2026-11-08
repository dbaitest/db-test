using DbTest.Core.Engine;
using DbTest.Core.Schema;
using DbTest.MsSql;
using DbTest.MsSql.Dacpac;
using DbTest.MSTest;

using Microsoft.Data.SqlClient;

namespace Sample.Database.Tests;

/// <summary>
/// Tests for the Sample.Database project, run against its own .dacpac deployed into a
/// throwaway SQL Server container.
/// </summary>
[TestClass]
public sealed class ShopTests : DbTestBase
{
    protected override DbEngine CreateEngine() => new MsSqlEngine();

    // The .dacpac is copied next to the test binaries by the CopyDacpacs target in the .csproj.
    protected override SchemaSource? SchemaSource =>
        new DacpacSchemaSource(Path.Combine(AppContext.BaseDirectory, "Sample.Database.dacpac"));

    /// <summary>
    /// Starts every test from the deployed state: CleanAsync empties every table — including the
    /// reference data the post-deployment script seeded — so the script runs again.
    /// </summary>
    [TestInitialize]
    public async Task ResetAsync()
    {
        await Harness.CleanAsync(TestContext.CancellationToken);

        var postDeployment = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Script.PostDeployment.sql"),
            TestContext.CancellationToken);
        await Harness.ExecuteAsync(postDeployment, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task PlaceOrder_Creates_The_Customer_And_The_Order()
    {
        var orderId = await PlaceOrderAsync("ada@example.test", "NL", 100.00m);

        Assert.AreEqual(1, orderId);
        Assert.AreEqual(1, await Harness.QueryScalarAsync<int>("SELECT COUNT(*) FROM dbo.Customers", TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task PlaceOrder_Reuses_An_Existing_Customer()
    {
        await PlaceOrderAsync("ada@example.test", "NL", 10.00m);
        await PlaceOrderAsync("ada@example.test", "DE", 20.00m);

        Assert.AreEqual(1, await Harness.QueryScalarAsync<int>("SELECT COUNT(*) FROM dbo.Customers", TestContext.CancellationToken));
        Assert.AreEqual(2, await Harness.QueryScalarAsync<int>("SELECT COUNT(*) FROM dbo.Orders", TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Computed_Tax_Uses_The_Regional_Rate()
    {
        var orderId = await PlaceOrderAsync("ada@example.test", "NL", 100.00m);

        var tax = await Harness.QueryScalarAsync<decimal>(
            "SELECT Tax FROM dbo.Orders WHERE Id = @id",
            [new DbParameterValue("@id", orderId)],
            TestContext.CancellationToken);

        Assert.AreEqual(21.00m, tax);
    }

    [TestMethod]
    public async Task View_Totals_Orders_Per_Customer()
    {
        await PlaceOrderAsync("ada@example.test", "NL", 10.00m);
        await PlaceOrderAsync("ada@example.test", "NL", 15.50m);
        await PlaceOrderAsync("grace@example.test", "GB", 99.00m);

        var total = await Harness.QueryScalarAsync<decimal>(
            """
            SELECT t.TotalAmount
            FROM dbo.CustomerOrderTotals AS t
            JOIN dbo.Customers AS c ON c.Id = t.CustomerId
            WHERE c.Email = @email
            """,
            [new DbParameterValue("@email", "ada@example.test")],
            TestContext.CancellationToken);

        Assert.AreEqual(25.50m, total);
    }

    [TestMethod]
    public async Task Clean_Resets_The_Identity_Of_A_Referenced_Table()
    {
        // Customers is referenced by a foreign key, so it is emptied with DELETE and reseeded
        // rather than truncated; the next customer still gets id 1.
        await PlaceOrderAsync("ada@example.test", "NL", 10.00m);
        await ResetAsync();

        await PlaceOrderAsync("grace@example.test", "GB", 10.00m);

        Assert.AreEqual(1, await Harness.QueryScalarAsync<int>("SELECT MIN(Id) FROM dbo.Customers", TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ListShopTables_Reads_The_Catalog()
    {
        // dbo.ListShopTables uses sys.* through the project's master database reference.
        await using var connection = (SqlConnection)await Harness.OpenConnectionAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("dbo.ListShopTables", connection) { CommandType = System.Data.CommandType.StoredProcedure };
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var tables = new List<string>();
        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        CollectionAssert.AreEqual(new[] { "dbo.Customers", "dbo.Orders", "dbo.TaxRates" }, tables);
    }

    [TestMethod]
    public async Task Shape_Guard_Catches_A_Reshaped_Table()
    {
        // Break a deployed table on purpose, prove the guard sees it, and put it back: the base
        // class runs the same check after every test and fails whichever test leaves it broken.
        await Harness.ExecuteAsync("ALTER TABLE dbo.Customers ADD Probe INT NULL", TestContext.CancellationToken);
        try
        {
            var leaks = await Harness.CheckShapeAsync(nameof(Shape_Guard_Catches_A_Reshaped_Table), TestContext.CancellationToken);

            Assert.HasCount(1, leaks);
            Assert.Contains("dbo.Customers", leaks[0].ToString());
        }
        finally
        {
            await Harness.ExecuteAsync("ALTER TABLE dbo.Customers DROP COLUMN Probe", TestContext.CancellationToken);
        }
    }

    private async Task<int> PlaceOrderAsync(string email, string regionCode, decimal amount)
    {
        await using var connection = (SqlConnection)await Harness.OpenConnectionAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("dbo.PlaceOrder", connection) { CommandType = System.Data.CommandType.StoredProcedure };

        command.Parameters.AddWithValue("@email", email);
        command.Parameters.AddWithValue("@regionCode", regionCode);
        command.Parameters.AddWithValue("@amount", amount);
        var orderId = command.Parameters.Add("@orderId", System.Data.SqlDbType.Int);
        orderId.Direction = System.Data.ParameterDirection.Output;

        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        return (int)orderId.Value;
    }
}
