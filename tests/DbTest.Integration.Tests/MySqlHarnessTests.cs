namespace DbTest.Integration.Tests;

/// <summary>Runs the shared harness behaviour against MySQL.</summary>
[TestClass]
public sealed class MySqlHarnessTests : HarnessBehaviourTestsBase
{
    protected override string EngineName => "mysql";

    protected override string AddProbeColumnSql => "ALTER TABLE customers ADD COLUMN dbtest_shape_probe INT NULL";

    protected override string DropProbeColumnSql => "ALTER TABLE customers DROP COLUMN dbtest_shape_probe";
}
