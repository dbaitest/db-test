using DbTest.Core.Engine;
using DbTest.Core.Schema;
using DbTest.MSTest;
using DbTest.PostgreSql;

namespace DbTest.Core.Tests;

[TestClass]
public sealed class DbTestRunKeyTests
{
    private static readonly PostgreSqlEngine Engine = new();

    [TestMethod]
    public void Equal_Options_Share_A_Harness()
    {
        Assert.AreEqual(
            DbTestRun.BuildKey(Engine, "/schema", new DbContainerOptions { Environment = { ["A"] = "1", ["B"] = "2" } }, null),
            DbTestRun.BuildKey(Engine, "/schema", new DbContainerOptions { Environment = { ["B"] = "2", ["A"] = "1" } }, null),
            "The order environment variables were added in does not matter.");
    }

    [TestMethod]
    public void Every_Option_That_Changes_The_Container_Or_Baseline_Separates_Harnesses()
    {
        var baseline = DbTestRun.BuildKey(Engine, "/schema", new DbContainerOptions(), new SchemaLoadOptions());

        string[] variants =
        [
            DbTestRun.BuildKey(Engine, "/other", new DbContainerOptions(), new SchemaLoadOptions()),
            DbTestRun.BuildKey(Engine, "/schema", new DbContainerOptions { Image = "postgres:17" }, new SchemaLoadOptions()),
            DbTestRun.BuildKey(Engine, "/schema", new DbContainerOptions { Database = "other" }, new SchemaLoadOptions()),
            DbTestRun.BuildKey(Engine, "/schema", new DbContainerOptions { Password = "secret" }, new SchemaLoadOptions()),
            DbTestRun.BuildKey(Engine, "/schema", new DbContainerOptions { Environment = { ["TZ"] = "UTC" } }, new SchemaLoadOptions()),
            DbTestRun.BuildKey(Engine, "/schema", new DbContainerOptions(), new SchemaLoadOptions { ExcludedDirectories = ["seed"] }),
            DbTestRun.BuildKey(Engine, "/schema", new DbContainerOptions(), new SchemaLoadOptions { ExcludedFileNamePatterns = ["^x"] }),
            DbTestRun.BuildKey(Engine, "/schema", new DbContainerOptions(), new SchemaLoadOptions { OrderedDirectories = ["tables"] }),
        ];

        foreach (var variant in variants)
        {
            Assert.AreNotEqual(baseline, variant);
        }
    }
}
