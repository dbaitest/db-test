using DbTest.Cli;

namespace DbTest.Core.Tests;

[TestClass]
public sealed class CliArgumentsTests
{
    [TestMethod]
    public void Parses_A_Command_And_Its_Options()
    {
        var parsed = CliArguments.Parse(["up", "--engine", "mysql", "--schema", "Schema/mysql", "--database", "app"]);

        Assert.AreEqual("up", parsed.Command);
        Assert.AreEqual("mysql", parsed.RequireEngine());
        Assert.AreEqual("Schema/mysql", parsed.Schema);
        Assert.AreEqual("app", parsed.Database);
        Assert.IsNull(parsed.Password);
    }

    [TestMethod]
    public void No_Arguments_Means_Help()
    {
        Assert.AreEqual("help", CliArguments.Parse([]).Command);
        Assert.AreEqual("help", CliArguments.Parse(["up", "--help"]).Command);
    }

    [TestMethod]
    public void Rejects_An_Unknown_Option()
    {
        var error = Assert.ThrowsExactly<CliUsageException>(() => CliArguments.Parse(["schema", "--shema", "x"]));

        StringAssert.Contains(error.Message, "--shema");
    }

    [TestMethod]
    [DataRow("--engine")]
    [DataRow("--engine", "--schema", "x")]
    public void Rejects_An_Option_Without_A_Value(params string[] options)
    {
        var error = Assert.ThrowsExactly<CliUsageException>(() => CliArguments.Parse(["up", .. options]));

        StringAssert.Contains(error.Message, "needs a value");
    }

    [TestMethod]
    public void Requires_An_Engine_Where_The_Command_Needs_One()
    {
        var parsed = CliArguments.Parse(["up"]);

        Assert.ThrowsExactly<CliUsageException>(() => parsed.RequireEngine());
    }
}
