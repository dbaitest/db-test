using DbTest.Core.Engine;
using DbTest.Core.Schema;
using DbTest.MsSql;
using DbTest.MsSql.Dacpac;
using DbTest.PostgreSql;

using Microsoft.SqlServer.Dac;

namespace DbTest.Core.Tests;

[TestClass]
public sealed class DacpacSchemaSourceTests
{
    private static string MissingPath => Path.Combine(Path.GetTempPath(), $"dbtest-missing-{Guid.NewGuid():N}.dacpac");

    [TestMethod]
    public void Key_Of_A_Missing_Dacpac_Does_Not_Throw()
    {
        // The key is built before any container starts; a missing file must be reported by
        // Validate with a useful message, not by a FileNotFoundException from a getter.
        var source = new DacpacSchemaSource(MissingPath);

        Assert.StartsWith("dacpac", source.Key);
    }

    [TestMethod]
    public void Equal_Options_Give_Equal_Keys()
    {
        var path = MissingPath;

        var a = new DacpacSchemaSource(path, Options("Env", "Test"));
        var b = new DacpacSchemaSource(path, Options("Env", "Test"));

        Assert.AreEqual(a.Key, b.Key);
    }

    [TestMethod]
    public void Every_Deploy_Option_That_Changes_The_Schema_Separates_Keys()
    {
        var path = MissingPath;
        var baseline = new DacpacSchemaSource(path, Options("Env", "Test")).Key;

        string[] variants =
        [
            new DacpacSchemaSource(path).Key,
            new DacpacSchemaSource(MissingPath, Options("Env", "Test")).Key,
            new DacpacSchemaSource(path, Options("Env", "Ci")).Key,
            new DacpacSchemaSource(path, Options("Other", "Test")).Key,
            new DacpacSchemaSource(path, WithFlag(Options("Env", "Test"))).Key,
            new DacpacSchemaSource(path, WithExcludedType(Options("Env", "Test"))).Key,
        ];

        foreach (var variant in variants)
        {
            Assert.AreNotEqual(baseline, variant);
        }
    }

    [TestMethod]
    public void A_Value_Cannot_Imitate_A_Separator()
    {
        var path = MissingPath;

        var one = new DacDeployOptions();
        one.SqlCommandVariableValues["A"] = "1,B:2";

        var two = new DacDeployOptions();
        two.SqlCommandVariableValues["A"] = "1";
        two.SqlCommandVariableValues["B"] = "2";

        Assert.AreNotEqual(new DacpacSchemaSource(path, one).Key, new DacpacSchemaSource(path, two).Key);
    }

    [TestMethod]
    public void Key_Does_Not_Carry_Option_Values()
    {
        // SQLCMD values and storage keys can be secrets; the key holds only a hash of them.
        var options = Options("Secret", "Hunter2-Value");

        Assert.DoesNotContain("Hunter2-Value", new DacpacSchemaSource(MissingPath, options).Key);
    }

    [TestMethod]
    public void A_Relative_Path_Resolves_Against_The_Current_Directory()
    {
        var source = new DacpacSchemaSource("Shop.dacpac");

        Assert.AreEqual(Path.GetFullPath("Shop.dacpac"), source.DacpacPath);
    }

    [TestMethod]
    public void A_Missing_Dacpac_Is_Reported_With_Its_Full_Path()
    {
        var path = MissingPath;
        var source = new DacpacSchemaSource(path);

        var error = Assert.ThrowsExactly<DbTestException>(() => source.Validate(new MsSqlEngine()));

        Assert.Contains(path, error.Message);
        Assert.Contains("SQL Server database projects", error.Message);
    }

    [TestMethod]
    public void Another_Engine_Is_Refused()
    {
        var source = new DacpacSchemaSource(MissingPath);

        var error = Assert.ThrowsExactly<DbTestException>(() => source.Validate(new PostgreSqlEngine()));

        Assert.Contains("SQL Server only", error.Message);
    }

    [TestMethod]
    public async Task A_File_That_Is_Not_A_Dacpac_Is_A_Failure_Not_An_Exception()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbtest-corrupt-{Guid.NewGuid():N}.dacpac");
        await File.WriteAllBytesAsync(path, [0x00, 0x01, 0x02, 0x03, 0x04]);

        try
        {
            var result = await new DacpacSchemaSource(path).ApplyAsync(Context());

            Assert.IsFalse(result.Succeeded);
            Assert.HasCount(1, result.Failures);
            Assert.AreEqual(Path.GetFileName(path), result.Failures[0].RelativePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Changing_The_Options_After_Construction_Is_Refused()
    {
        var options = Options("Env", "Test");
        var source = new DacpacSchemaSource(MissingPath, options);

        options.SqlCommandVariableValues["Env"] = "Prod";

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => source.ApplyAsync(Context()));
    }

    [TestMethod]
    public void Redact_Removes_The_Password()
    {
        const string connectionString = "Server=localhost;Database=db;User Id=sa;Password=Sup3r-Secret!";

        var text = DacpacSchemaSource.Redact("Login failed with Sup3r-Secret! for sa", connectionString);

        Assert.DoesNotContain("Sup3r-Secret!", text);
        Assert.Contains("[redacted]", text);
    }

    [TestMethod]
    public void Default_Options_Are_Bounded_By_The_Engine_Timeout()
    {
        var engine = new MsSqlEngine();

        var options = DacpacSchemaSource.CreateDefaultOptions(engine);

        Assert.AreEqual((int)engine.CommandTimeout.TotalSeconds, options.CommandTimeout);
        Assert.AreEqual((int)engine.CommandTimeout.TotalSeconds, options.LongRunningCommandTimeout);
        Assert.IsFalse(options.ScriptDatabaseOptions);
        Assert.IsFalse(options.CreateNewDatabase);
        Assert.IsFalse(options.DropObjectsNotInSource);
    }

    private static DacDeployOptions Options(string variable, string value)
    {
        var options = new DacDeployOptions();
        options.SqlCommandVariableValues[variable] = value;
        return options;
    }

    private static DacDeployOptions WithFlag(DacDeployOptions options)
    {
        options.IgnoreColumnOrder = !options.IgnoreColumnOrder;
        return options;
    }

    private static DacDeployOptions WithExcludedType(DacDeployOptions options)
    {
        options.ExcludeObjectTypes = [ObjectType.Users];
        return options;
    }

    private static SchemaApplyContext Context() => new()
    {
        Engine = new MsSqlEngine(),
        ConnectionString = "Server=localhost,1;Database=db;User Id=sa;Password=Sup3r-Secret!;Connect Timeout=1",
        Database = "db",
    };
}
