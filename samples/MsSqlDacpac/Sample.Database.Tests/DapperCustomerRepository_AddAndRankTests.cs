using Microsoft.Data.SqlClient;

using Sample.Database.Tests.Fixtures;
using Sample.Shop.Customers;

namespace Sample.Database.Tests;

/// <summary>
/// Drives the application's Dapper repository, <see cref="DapperCustomerRepository"/>, against
/// the deployed database: parameter names, the identity it returns, how a duplicate key
/// surfaces, and the column-to-record mapping of a query over a view.
/// </summary>
[TestClass]
public sealed class DapperCustomerRepository_AddAndRankTests : ShopDatabaseTest
{
    private DapperCustomerRepository Customers => new(Connections);

    /// <summary>
    /// Kills: an insert that returns something other than the new identity, or a lookup bound to
    /// the wrong parameter — the unknown address must still come back as null.
    /// </summary>
    [TestMethod]
    public async Task Added_Customer_Is_Found_By_Email()
    {
        var id = await Customers.AddAsync("ada@example.test", TestContext.CancellationToken);

        Assert.AreEqual(1, id);
        Assert.AreEqual(1, await Customers.FindIdByEmailAsync("ada@example.test", TestContext.CancellationToken));
        Assert.IsNull(await Customers.FindIdByEmailAsync("nobody@example.test", TestContext.CancellationToken));
        Assert.AreEqual(1, await Harness.QueryScalarAsync<int>("SELECT COUNT(*) FROM dbo.Customers", TestContext.CancellationToken));
    }

    /// <summary>
    /// Kills: a raw SqlException leaking out of the repository instead of the domain exception
    /// callers catch, or a duplicate that is inserted anyway.
    /// </summary>
    [TestMethod]
    public async Task Duplicate_Email_Throws_A_Domain_Exception()
    {
        await Customers.AddAsync("ada@example.test", TestContext.CancellationToken);

        var ex = await Assert.ThrowsExactlyAsync<DuplicateCustomerException>(
            () => Customers.AddAsync("ada@example.test", TestContext.CancellationToken));

        Assert.AreEqual("ada@example.test", ex.Email);
        Assert.AreEqual(2627, Assert.IsInstanceOfType<SqlException>(ex.InnerException).Number);
        Assert.AreEqual(1, await Harness.QueryScalarAsync<int>("SELECT COUNT(*) FROM dbo.Customers", TestContext.CancellationToken));
    }

    /// <summary>
    /// Kills: a wrong ORDER BY or join. The JSON fixture gives three customers three different
    /// totals, so no ordering but the right one passes, and every field of every row is checked.
    /// </summary>
    [TestMethod]
    public async Task Totals_Rank_Customers_By_Spend()
    {
        await ShopFixtures.LoadJsonAsync(Harness, "shop-data.json", TestContext.CancellationToken);

        var totals = await Customers.ListTotalsAsync(TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            new[]
            {
                new CustomerTotal("grace@example.test", 1, 99.00m),
                new CustomerTotal("ada@example.test", 2, 50.00m),
                new CustomerTotal("linus@example.test", 1, 5.00m),
            },
            totals.ToArray());
    }
}
