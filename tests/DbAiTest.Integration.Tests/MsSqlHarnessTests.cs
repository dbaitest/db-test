using DbAiTest.Core.Engine;
using DbAiTest.MsSql;

namespace DbAiTest.Integration.Tests;

/// <summary>Runs the shared harness behaviour against SQL Server.</summary>
[TestClass]
public sealed class MsSqlHarnessTests : HarnessBehaviourTestsBase
{
    protected override string SchemaDirectory => "mssql";

    // T-SQL spells this "ADD", not "ADD COLUMN".
    protected override string AddProbeColumnSql => "ALTER TABLE dbo.customers ADD dbaitest_shape_probe INT NULL";

    protected override string DropProbeColumnSql => "ALTER TABLE dbo.customers DROP COLUMN dbaitest_shape_probe";

    protected override DbEngine CreateEngine() => new MsSqlEngine();
}
