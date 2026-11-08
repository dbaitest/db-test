using DbTest.Core.Engine;
using DbTest.PostgreSql;

namespace DbTest.Integration.Tests;

/// <summary>Runs the shared harness behaviour against PostgreSQL.</summary>
[TestClass]
public sealed class PostgreSqlHarnessTests : HarnessBehaviourTestsBase
{
    protected override string SchemaDirectory => "postgres";

    protected override string AddProbeColumnSql => "ALTER TABLE customers ADD COLUMN dbtest_shape_probe INT NULL";

    protected override string DropProbeColumnSql => "ALTER TABLE customers DROP COLUMN dbtest_shape_probe";

    protected override DbEngine CreateEngine() => new PostgreSqlEngine();
}
