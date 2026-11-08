using Sample.Database.Tests.Fixtures;
using Sample.Shop.Orders;

namespace Sample.Database.Tests;

/// <summary>
/// What each fixture format guarantees, shown through the application's repository: SQL
/// fixtures rely on CleanAsync reseeding identities, JSON fixtures carry their own ids, and a
/// fixture that cannot be loaded fails the test instead of leaving the database empty.
/// </summary>
[TestClass]
public sealed class ShopFixtures_SqlAndJsonTests : ShopDatabaseTest
{
    private SqlOrderRepository Orders => new(Connections);

    /// <summary>
    /// Kills: a test that depends on ids which drift between runs. CleanAsync puts identities back
    /// to their seed, so the fixture's third order is id 3 on every run.
    /// </summary>
    [TestMethod]
    public async Task Sql_Fixture_Ids_Are_Stable_Because_Clean_Reseeds()
    {
        await ShopFixtures.LoadSqlAsync(Harness, "customers-with-orders.sql", TestContext.CancellationToken);

        var order = await Orders.GetAsync(3, TestContext.CancellationToken);

        // Customer 1 is ada; DE tax is 19 %.
        Assert.AreEqual(new Order(3, 1, "DE", 20.00m, 3.80m), order);
    }

    /// <summary>
    /// Kills: a loader that drops explicit ids. The JSON fixture's orders are 100-103; SQL Server
    /// moves the identity past them, so the next order is 104, and grace is not created twice.
    /// </summary>
    [TestMethod]
    public async Task Json_Fixture_Keeps_Its_Ids_And_New_Rows_Follow_Them()
    {
        await ShopFixtures.LoadJsonAsync(Harness, "shop-data.json", TestContext.CancellationToken);

        var id = await Orders.PlaceAsync(new NewOrder("grace@example.test", "GB", 1.00m), TestContext.CancellationToken);

        Assert.AreEqual(104, id);
        Assert.AreEqual(new Order(104, 11, "GB", 1.00m, 0.20m), await Orders.GetAsync(id, TestContext.CancellationToken));
        Assert.AreEqual(3, await Harness.QueryScalarAsync<int>("SELECT COUNT(*) FROM dbo.Customers", TestContext.CancellationToken));
    }

    /// <summary>
    /// Kills: a loader that skips a missing file. A test whose fixture never loaded would run
    /// against an empty database and could pass for the wrong reason.
    /// </summary>
    [TestMethod]
    public async Task Missing_Fixture_Fails_Loudly()
    {
        var ex = await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => ShopFixtures.LoadJsonAsync(Harness, "no-such-fixture.json", TestContext.CancellationToken));

        Assert.Contains("no-such-fixture.json", ex.Message);
    }

    /// <summary>
    /// Kills: table or column names from a fixture reaching SQL unchecked, a nested value inserted
    /// as text, or a row the server rejects surfacing without saying which row it was. The error
    /// names the file and the row, and nothing is inserted.
    /// </summary>
    [TestMethod]
    [DataRow("""{ "dbo.Customers; DROP TABLE dbo.Orders": [ { "Email": "ada@example.test" } ] }""", "is not a plain table or column name")]
    [DataRow("""{ "dbo.Customers": [ { "Email": { "nested": true } } ] }""", "must be a string, number, boolean or null")]
    [DataRow("""{ "dbo.Orders": [ { "CustomerId": 999, "RegionCode": "NL", "Amount": 1.00 } ] }""", "dbo.Orders[0]: The INSERT statement conflicted with the FOREIGN KEY constraint")]
    public async Task Unsafe_Json_Fixture_Is_Rejected(string json, string expectedMessage)
    {
        var fileName = $"rejected-{Guid.NewGuid():N}.json";
        var path = Path.Join(AppContext.BaseDirectory, "Fixtures", fileName);
        await File.WriteAllTextAsync(path, json, TestContext.CancellationToken);
        try
        {
            var ex = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => ShopFixtures.LoadJsonAsync(Harness, fileName, TestContext.CancellationToken));

            Assert.Contains(fileName, ex.Message);
            Assert.Contains(expectedMessage, ex.Message);
            Assert.AreEqual(0, await Harness.QueryScalarAsync<int>("SELECT COUNT(*) FROM dbo.Customers", TestContext.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
