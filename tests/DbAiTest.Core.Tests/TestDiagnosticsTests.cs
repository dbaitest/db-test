using DbAiTest.Core.Diagnostics;

namespace DbAiTest.Core.Tests;

[TestClass]
public sealed class TestDiagnosticsTests
{
    private string? _previousEnable;
    private string? _previousPath;
    private string _file = null!;

    [TestInitialize]
    public void EnableDiagnostics()
    {
        _previousEnable = Environment.GetEnvironmentVariable(TestDiagnostics.EnableVariable);
        _previousPath = Environment.GetEnvironmentVariable(TestDiagnostics.PathVariable);
        _file = Path.Join(Path.GetTempPath(), "dbaitest-timing-" + Guid.NewGuid().ToString("N") + ".tsv");

        Environment.SetEnvironmentVariable(TestDiagnostics.EnableVariable, "1");
        Environment.SetEnvironmentVariable(TestDiagnostics.PathVariable, _file);
    }

    [TestCleanup]
    public void RestoreEnvironment()
    {
        Environment.SetEnvironmentVariable(TestDiagnostics.EnableVariable, _previousEnable);
        Environment.SetEnvironmentVariable(TestDiagnostics.PathVariable, _previousPath);
        File.Delete(_file);
    }

    [TestMethod]
    public void Is_Off_Unless_Enabled()
    {
        Environment.SetEnvironmentVariable(TestDiagnostics.EnableVariable, null);

        Assert.IsNull(TestDiagnostics.TryCreate(Path.GetTempPath()));
    }

    [TestMethod]
    public void Writes_Each_Row_Once_However_Often_It_Is_Flushed()
    {
        using (var diagnostics = TestDiagnostics.TryCreate(Path.GetTempPath())!)
        {
            diagnostics.Record("A.First", TimeSpan.FromMilliseconds(12.34));
            diagnostics.Flush();
            diagnostics.Record("A.Second", TimeSpan.FromMilliseconds(5));
            diagnostics.Flush();
        }

        CollectionAssert.AreEqual(
            new[] { "kind\tvalue\tdetail", "test\t12.3\tA.First", "test\t5.0\tA.Second" },
            File.ReadAllLines(_file));
    }

    [TestMethod]
    public void Writes_The_Header_Only_Into_A_New_File()
    {
        var first = TestDiagnostics.TryCreate(Path.GetTempPath())!;
        first.Record("A.One", TimeSpan.FromMilliseconds(1));
        first.Flush();

        var second = TestDiagnostics.TryCreate(Path.GetTempPath())!;
        second.RecordMetric("containers", 3, "started");
        second.Flush();

        CollectionAssert.AreEqual(
            new[] { "kind\tvalue\tdetail", "test\t1.0\tA.One", "containers\t3\tstarted" },
            File.ReadAllLines(_file));
    }
}
