using System.Transactions;

using Sample.Shop.Orders;

namespace Sample.Database.Tests;

/// <summary>
/// Rollback isolation: run the repository inside a <see cref="TransactionScope"/> that is never
/// completed, and its writes are undone when the scope is disposed. This class still derives from
/// <see cref="ShopDatabaseTest"/>, so ids start at 1. A suite that relies on the rollback alone
/// and skips the clean keys every row it writes to its own range, because the container is
/// shared by every test class in the run, and asserts on that range only — as the checks after
/// the scope do here with @rollback.example.test addresses.
/// </summary>
/// <remarks>
/// The repository opens one connection at a time. Two connections open together inside the scope
/// would promote it to a distributed transaction, which SqlClient does not support on Linux.
/// </remarks>
[TestClass]
public sealed class SqlOrderRepository_RollbackIsolationTests : ShopDatabaseTest
{
    private SqlOrderRepository Orders => new(Connections);

    /// <summary>
    /// Kills: a repository whose connections do not enlist in the caller's transaction (for
    /// example Enlist=false, or a suppressing scope of its own) — its writes would outlive the
    /// caller's rollback, and so would break every unit of work built on it.
    /// </summary>
    [TestMethod]
    public async Task Writes_Inside_An_Uncompleted_Scope_Are_Gone_After_It()
    {
        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var id = await Orders.PlaceAsync(new NewOrder("ada@rollback.example.test", "NL", 10.00m), TestContext.CancellationToken);

            // Inside the scope the write is visible to the same transaction.
            Assert.AreEqual(new Order(1, 1, "NL", 10.00m, 2.10m), await Orders.GetAsync(id, TestContext.CancellationToken));

            // No Complete(): disposing the scope rolls the transaction back.
        }

        // Read outside the scope, on a connection of the harness's own.
        Assert.AreEqual(0, await Harness.QueryScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM dbo.Orders AS o
            JOIN dbo.Customers AS c ON c.Id = o.CustomerId
            WHERE c.Email LIKE '%@rollback.example.test'
            """,
            TestContext.CancellationToken));
        Assert.AreEqual(0, await Harness.QueryScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Customers WHERE Email LIKE '%@rollback.example.test'",
            TestContext.CancellationToken));
    }
}
