using DbAiTest.Core.Engine;
using DbAiTest.Core.Schema;
using DbAiTest.MsSql;
using DbAiTest.MsSql.Dacpac;
using DbAiTest.MSTest;

using Sample.Shop;

namespace Sample.Database.Tests;

/// <summary>
/// Base class for tests that drive the application's own repositories (Sample.Shop) against the
/// deployed Sample.Database. It is the same setup as <see cref="ShopTests"/>, which keeps its own
/// copy so it stays readable on its own, plus <see cref="Connections"/>.
/// </summary>
public abstract class ShopDatabaseTest : DbAiTestBase
{
    protected override DbEngine CreateEngine() => new MsSqlEngine();

    // The same dacpac as ShopTests, so every test class shares one container.
    protected override SchemaSource? SchemaSource =>
        new DacpacSchemaSource(Path.Join(AppContext.BaseDirectory, "Sample.Database.dacpac"));

    /// <summary>
    /// The application's connection factory, pointed at the test container. Repositories are
    /// built with it exactly as the application builds them; only the connection string differs.
    /// </summary>
    protected IShopConnectionFactory Connections => new SqlConnectionFactory(Harness.ConnectionString);

    /// <summary>
    /// Starts every test from the deployed state: CleanAsync empties every table, including the
    /// tax rates the post-deployment script seeded, so the script runs again. MSTest runs this
    /// after <see cref="DbAiTestBase"/>'s own initialization, which creates the harness.
    /// </summary>
    [TestInitialize]
    public async Task ResetAsync()
    {
        await Harness.CleanAsync(TestContext.CancellationToken);

        var postDeployment = await File.ReadAllTextAsync(
            Path.Join(AppContext.BaseDirectory, "Script.PostDeployment.sql"),
            TestContext.CancellationToken);
        await Harness.ExecuteAsync(postDeployment, TestContext.CancellationToken);
    }
}
