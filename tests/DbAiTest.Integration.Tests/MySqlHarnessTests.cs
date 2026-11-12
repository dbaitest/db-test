using DbAiTest.Core.Engine;
using DbAiTest.MySql;

namespace DbAiTest.Integration.Tests;

/// <summary>Runs the shared harness behaviour against MySQL.</summary>
[TestClass]
public sealed class MySqlHarnessTests : HarnessBehaviourTestsBase
{
    protected override string SchemaDirectory => "mysql";

    protected override string AddProbeColumnSql => "ALTER TABLE customers ADD COLUMN dbaitest_shape_probe INT NULL";

    protected override string DropProbeColumnSql => "ALTER TABLE customers DROP COLUMN dbaitest_shape_probe";

    protected override DbEngine CreateEngine() => new MySqlEngine();
}
