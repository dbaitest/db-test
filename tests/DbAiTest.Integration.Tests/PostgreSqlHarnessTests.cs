using DbAiTest.Core.Engine;
using DbAiTest.PostgreSql;

namespace DbAiTest.Integration.Tests;

/// <summary>Runs the shared harness behaviour against PostgreSQL.</summary>
[TestClass]
public sealed class PostgreSqlHarnessTests : HarnessBehaviourTestsBase
{
    protected override string SchemaDirectory => "postgres";

    protected override string AddProbeColumnSql => "ALTER TABLE customers ADD COLUMN dbaitest_shape_probe INT NULL";

    protected override string DropProbeColumnSql => "ALTER TABLE customers DROP COLUMN dbaitest_shape_probe";

    protected override DbEngine CreateEngine() => new PostgreSqlEngine();
}
