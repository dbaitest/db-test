using DbTest.Core.Schema;

namespace DbTest.Core.Tests;

[TestClass]
public sealed class SchemaLoaderTests
{
    private string _root = null!;

    [TestInitialize]
    public void CreateSchemaDirectory()
    {
        _root = Path.Join(Path.GetTempPath(), "dbtest-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void DeleteSchemaDirectory() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void Orders_Files_By_Relative_Path_And_Ignores_Non_Sql_Files()
    {
        Write("tables/002_orders.sql");
        Write("tables/001_customers.sql");
        Write("001_bootstrap.psql");
        Write("procedures/total.sql");
        Write("README.md");

        var load = SchemaLoader.Load(_root);

        CollectionAssert.AreEqual(
            new[] { "001_bootstrap.psql", "procedures/total.sql", "tables/001_customers.sql", "tables/002_orders.sql" },
            load.Scripts.Select(s => s.RelativePath).ToArray());
    }

    [TestMethod]
    public void Applies_Ordered_Directories_First_Including_Their_Subdirectories()
    {
        Write("procedures/total.sql");
        Write("tables/core/customers.sql");
        Write("tables/orders.sql");
        Write("views/summary.sql");

        var load = SchemaLoader.Load(_root, new SchemaLoadOptions { OrderedDirectories = ["tables", "views"] });

        CollectionAssert.AreEqual(
            new[] { "tables/core/customers.sql", "tables/orders.sql", "views/summary.sql", "procedures/total.sql" },
            load.Scripts.Select(s => s.RelativePath).ToArray());
    }

    [TestMethod]
    public void Reports_Every_Excluded_File_With_Its_Reason()
    {
        Write("tables/customers.sql");
        Write("maintenance/rebuild.sql");
        Write("tables/fix_customers.sql");

        var load = SchemaLoader.Load(_root, new SchemaLoadOptions
        {
            ExcludedDirectories = ["maintenance"],
            ExcludedFileNamePatterns = ["^fix_"],
        });

        CollectionAssert.AreEqual(new[] { "tables/customers.sql" }, load.Scripts.Select(s => s.RelativePath).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "maintenance/rebuild.sql:directory-excluded", "tables/fix_customers.sql:filename-excluded" },
            load.Excluded.Select(e => $"{e.RelativePath}:{e.Reason}").ToArray());
    }

    [TestMethod]
    public void Throws_When_The_Root_Does_Not_Exist()
    {
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => SchemaLoader.Load(Path.Join(_root, "missing")));
    }

    private void Write(string relativePath)
    {
        var path = Path.Join(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "SELECT 1;");
    }
}
