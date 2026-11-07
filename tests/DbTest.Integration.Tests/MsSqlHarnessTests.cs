namespace DbTest.Integration.Tests;

/// <summary>Runs the shared harness behaviour against SQL Server.</summary>
[TestClass]
public sealed class MsSqlHarnessTests : HarnessBehaviourTestsBase
{
    protected override string EngineName => "mssql";

    // T-SQL spells this "ADD", not "ADD COLUMN".
    protected override string AddProbeColumnSql => "ALTER TABLE dbo.customers ADD dbtest_shape_probe INT NULL";

    protected override string DropProbeColumnSql => "ALTER TABLE dbo.customers DROP COLUMN dbtest_shape_probe";
}
