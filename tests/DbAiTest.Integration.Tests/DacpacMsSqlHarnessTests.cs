using DbAiTest.Core;
using DbAiTest.Core.Engine;
using DbAiTest.Core.Schema;
using DbAiTest.MsSql;
using DbAiTest.MsSql.Dacpac;
using DbAiTest.MSTest;

using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

namespace DbAiTest.Integration.Tests;

/// <summary>
/// Deploys a real dacpac into SQL Server through <see cref="DacpacSchemaSource"/>, on every
/// target framework. The name contains <c>MsSqlHarnessTests</c> so the SQL Server CI leg's
/// filter picks it up.
/// </summary>
/// <remarks>
/// The dacpac is built in-process from a model, so the test needs no database project. Its
/// objects depend on each other across what would be separate folders in both directions —
/// <c>Orders</c> has a computed column calling <c>TaxFor</c>, which reads <c>TaxRates</c> —
/// which is the case a file-ordered schema cannot apply.
/// </remarks>
[TestClass]
public sealed class DacpacMsSqlHarnessTests : DbAiTestBase
{
    private static readonly string[] ShopObjects =
    [
        """
        CREATE TABLE dbo.TaxRates (
            RegionCode CHAR(2) NOT NULL CONSTRAINT PK_TaxRates PRIMARY KEY,
            Rate DECIMAL(5, 4) NOT NULL)
        """,
        """
        CREATE FUNCTION dbo.TaxFor (@regionCode CHAR(2), @amount DECIMAL(18, 2))
        RETURNS DECIMAL(18, 2)
        WITH SCHEMABINDING
        AS
        BEGIN
            RETURN (SELECT CAST(@amount * r.Rate AS DECIMAL(18, 2)) FROM dbo.TaxRates AS r WHERE r.RegionCode = @regionCode);
        END
        """,
        """
        CREATE TABLE dbo.Customers (
            Id INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
            Email NVARCHAR(200) NOT NULL CONSTRAINT UQ_Customers_Email UNIQUE)
        """,
        """
        CREATE TABLE dbo.Orders (
            Id INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
            CustomerId INT NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Customers (Id),
            RegionCode CHAR(2) NOT NULL,
            Amount DECIMAL(18, 2) NOT NULL,
            Tax AS dbo.TaxFor(RegionCode, Amount))
        """,
        """
        CREATE VIEW dbo.CustomerOrderTotals
        WITH SCHEMABINDING
        AS
        SELECT o.CustomerId, COUNT_BIG(*) AS OrderCount, SUM(o.Amount) AS TotalAmount
        FROM dbo.Orders AS o
        GROUP BY o.CustomerId
        """,
    ];

    private static string _dacpacPath = string.Empty;

    protected override SchemaSource? SchemaSource => new DacpacSchemaSource(_dacpacPath);

    protected override DbEngine CreateEngine() => new MsSqlEngine();

    [ClassInitialize]
    public static void BuildDacpac(TestContext context)
    {
        _dacpacPath = BuildPackage("DacpacShop", ShopObjects);
    }

    [ClassCleanup]
    public static void DeleteDacpac()
    {
        File.Delete(_dacpacPath);
    }

    [TestMethod]
    public async Task Dacpac_Objects_Should_Be_Deployed()
    {
        var objects = await Harness.ListObjectsAsync(TestContext.CancellationToken);
        var names = objects.Select(o => o.QualifiedName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var expected in new[] { "dbo.TaxRates", "dbo.TaxFor", "dbo.Customers", "dbo.Orders", "dbo.CustomerOrderTotals" })
        {
            Assert.Contains(expected, names);
        }
    }

    [TestMethod]
    public async Task Computed_Column_Should_Call_The_Schemabound_Function()
    {
        await Harness.CleanAsync(TestContext.CancellationToken);
        await Harness.ExecuteAsync(
            """
            INSERT INTO dbo.TaxRates (RegionCode, Rate) VALUES ('NL', 0.2100);
            INSERT INTO dbo.Customers (Email) VALUES (N'ada@example.test');
            INSERT INTO dbo.Orders (CustomerId, RegionCode, Amount) VALUES (1, 'NL', 100.00);
            """,
            TestContext.CancellationToken);

        var tax = await Harness.QueryScalarAsync<decimal>("SELECT Tax FROM dbo.Orders", TestContext.CancellationToken);

        Assert.AreEqual(21.00m, tax);
    }

    [TestMethod]
    public async Task Clean_Should_Reset_The_Identity_Of_A_Referenced_Table()
    {
        await Harness.ExecuteAsync("INSERT INTO dbo.Customers (Email) VALUES (N'one@example.test')", TestContext.CancellationToken);
        await Harness.CleanAsync(TestContext.CancellationToken);
        await Harness.ExecuteAsync("INSERT INTO dbo.Customers (Email) VALUES (N'two@example.test')", TestContext.CancellationToken);

        var id = await Harness.QueryScalarAsync<int>("SELECT Id FROM dbo.Customers", TestContext.CancellationToken);

        Assert.AreEqual(1, id);
    }

    [TestMethod]
    public async Task Shape_Guard_Should_Watch_Dacpac_Objects()
    {
        await Harness.ExecuteAsync("ALTER TABLE dbo.Customers ADD dbaitest_shape_probe INT NULL", TestContext.CancellationToken);
        try
        {
            var leaks = await Harness.CheckShapeAsync("probe", TestContext.CancellationToken);

            Assert.HasCount(1, leaks);
            Assert.Contains("dbo.Customers", leaks[0].ToString());
        }
        finally
        {
            await Harness.ExecuteAsync("ALTER TABLE dbo.Customers DROP COLUMN dbaitest_shape_probe", TestContext.CancellationToken);
        }

        Assert.IsEmpty(await Harness.CheckShapeAsync("probe", TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task A_Deploy_The_Server_Rejects_Should_Fail_Setup_Without_The_Password()
    {
        // A memory-optimized table is a valid model object, but the server refuses it in a
        // database without a MEMORY_OPTIMIZED_DATA filegroup — which the container's has not.
        var path = BuildPackage(
            "DacpacRejected",
            [
                """
                CREATE TABLE dbo.HotRows (
                    Id INT NOT NULL CONSTRAINT PK_HotRows PRIMARY KEY NONCLUSTERED)
                WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_ONLY)
                """,
            ]);
        const string password = "Rejected-Deploy-9!";

        try
        {
            var error = await Assert.ThrowsExactlyAsync<SchemaApplyException>(() => DbAiTestHarness.StartAsync(
                new MsSqlEngine(),
                new DacpacSchemaSource(path),
                new DbContainerOptions { Password = password },
                TestContext.CancellationToken));

            Assert.Contains(Path.GetFileName(path), error.Message);
            Assert.Contains("memory", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(password, error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string BuildPackage(string name, IEnumerable<string> objects)
    {
        var path = Path.Join(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}.dacpac");

        using var model = new TSqlModel(SqlServerVersion.Sql160, new TSqlModelOptions());
        foreach (var definition in objects)
        {
            model.AddObjects(definition);
        }

        DacPackageExtensions.BuildPackage(path, model, new PackageMetadata { Name = name, Version = "1.0.0" });
        return path;
    }
}
