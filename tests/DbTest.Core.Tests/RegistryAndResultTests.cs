using DbTest.Cli;
using DbTest.Core.Engine;
using DbTest.Core.Schema;

namespace DbTest.Core.Tests;

[TestClass]
public sealed class RegistryAndResultTests
{
    [TestMethod]
    public void Registry_Resolves_Builtin_Engines_Case_Insensitively()
    {
        EngineRegistration.RegisterBuiltins();

        Assert.AreEqual("postgres", EngineRegistry.Create("Postgres").Name);
        CollectionAssert.IsSubsetOf(new[] { "mssql", "mysql", "postgres" }, EngineRegistry.Names.ToArray());
    }

    [TestMethod]
    public void Registry_Names_The_Registered_Engines_When_The_Name_Is_Unknown()
    {
        EngineRegistration.RegisterBuiltins();

        var error = Assert.ThrowsExactly<DbTestException>(() => EngineRegistry.Create("oracle"));

        StringAssert.Contains(error.Message, "mssql, mysql, postgres");
    }

    [TestMethod]
    public void Apply_Result_Lists_Every_Failure_When_Thrown()
    {
        var result = new SchemaApplyResult(
            ["001_tables.sql"],
            [new SchemaApplyFailure("002_views.sql", "invalid object"), new SchemaApplyFailure("003_procs.sql", "syntax")],
            TimeSpan.FromSeconds(1));

        var error = Assert.ThrowsExactly<SchemaApplyException>(result.ThrowIfFailed);

        Assert.HasCount(2, error.Failures);
        StringAssert.Contains(error.Message, "002_views.sql: invalid object");
        StringAssert.Contains(error.Message, "003_procs.sql: syntax");
    }

    [TestMethod]
    public void Apply_Result_Does_Not_Throw_When_Everything_Applied()
    {
        var result = new SchemaApplyResult(["001_tables.sql"], [], TimeSpan.Zero);

        Assert.IsTrue(result.Succeeded);
        result.ThrowIfFailed();
    }
}
