using DbTest.Core.Engine;
using DbTest.Core.Schema;
using DbTest.MSTest;
using DbTest.PostgreSql;

namespace DbTest.Core.Tests;

[TestClass]
public sealed class DbTestRunKeyTests
{
    private static readonly PostgreSqlEngine Engine = new();

    private static string Key(string root, DbContainerOptions options, SchemaLoadOptions? load = null)
        => DbTestRun.BuildKey(Engine, new DirectorySchemaSource(root, load).Key, options);

    [TestMethod]
    public void Equal_Options_Share_A_Harness()
    {
        Assert.AreEqual(
            Key("/schema", new DbContainerOptions { Environment = { ["A"] = "1", ["B"] = "2" } }),
            Key("/schema", new DbContainerOptions { Environment = { ["B"] = "2", ["A"] = "1" } }),
            "The order environment variables were added in does not matter.");
    }

    [TestMethod]
    public void Every_Option_That_Changes_The_Container_Or_Baseline_Separates_Harnesses()
    {
        var baseline = Key("/schema", new DbContainerOptions(), new SchemaLoadOptions());

        string[] variants =
        [
            Key("/other", new DbContainerOptions(), new SchemaLoadOptions()),
            Key("/schema", new DbContainerOptions { Image = "postgres:17" }, new SchemaLoadOptions()),
            Key("/schema", new DbContainerOptions { Database = "other" }, new SchemaLoadOptions()),
            Key("/schema", new DbContainerOptions { Password = "secret" }, new SchemaLoadOptions()),
            Key("/schema", new DbContainerOptions { Environment = { ["TZ"] = "UTC" } }, new SchemaLoadOptions()),
            Key("/schema", new DbContainerOptions(), new SchemaLoadOptions { ExcludedDirectories = ["seed"] }),
            Key("/schema", new DbContainerOptions(), new SchemaLoadOptions { ExcludedFileNamePatterns = ["^x"] }),
            Key("/schema", new DbContainerOptions(), new SchemaLoadOptions { OrderedDirectories = ["tables"] }),
            DbTestRun.BuildKey(Engine, null, new DbContainerOptions()),
        ];

        foreach (var variant in variants)
        {
            Assert.AreNotEqual(baseline, variant);
        }
    }

    [TestMethod]
    public void SchemaRoot_And_An_Equivalent_Directory_Source_Share_A_Harness()
    {
        // What GetHarnessAsync builds from SchemaRoot => "Schema/mssql" ...
        var fromRoot = new DirectorySchemaSource(Path.GetFullPath("Schema/mssql", AppContext.BaseDirectory));

        // ... and what a test writes when it overrides Schema instead.
        var explicitSource = new DirectorySchemaSource(Path.Combine(AppContext.BaseDirectory, "Schema/mssql"));

        Assert.AreEqual(fromRoot.Key, explicitSource.Key);
    }
}
