# db-test

[![Build](https://github.com/dbaitest/db-test/actions/workflows/ci.yml/badge.svg)](https://github.com/dbaitest/db-test/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/DbTest.Core.svg)](https://www.nuget.org/packages/DbTest.Core)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-dbaitest.com-0b7285.svg)](https://dbaitest.com/db-test/)

**Documentation: [dbaitest.com/db-test](https://dbaitest.com/db-test/)** (English and Tiếng Việt)

A .NET library for **database integration testing** that runs the same test body against
**SQL Server, PostgreSQL and MySQL**.

Every run gets a throwaway database container. Nothing touches a shared, staging or
production database.

```csharp
using DbTest.Core.Engine;
using DbTest.MsSql;
using DbTest.MSTest;
using DbTest.MySql;
using DbTest.PostgreSql;

// The tests, written once. No engine name appears in them.
public abstract class CustomerTests : DbTestBase
{
    [TestMethod]
    public async Task Inserted_Customer_Should_Be_Counted()
    {
        await Harness.CleanAsync();
        await Harness.ExecuteAsync(
            "INSERT INTO customers (email, display_name) VALUES (@email, @name)",
            [new DbParameterValue("@email", "ada@example.test"), new DbParameterValue("@name", "Ada")]);

        var count = await Harness.QueryScalarAsync<int>(
            "SELECT COUNT(*) FROM customers WHERE email = @email",
            [new DbParameterValue("@email", "ada@example.test")]);

        Assert.AreEqual(1, count);
    }
}

// One class per engine: which engine, and where its schema lives.
[TestClass]
public sealed class PostgresCustomerTests : CustomerTests
{
    protected override DbEngine CreateEngine() => new PostgreSqlEngine();
    protected override string SchemaRoot => "Schema/postgres";
}

[TestClass]
public sealed class MsSqlCustomerTests : CustomerTests
{
    protected override DbEngine CreateEngine() => new MsSqlEngine();
    protected override string SchemaRoot => "Schema/mssql";
}

[TestClass]
public sealed class MySqlCustomerTests : CustomerTests
{
    protected override DbEngine CreateEngine() => new MySqlEngine();
    protected override string SchemaRoot => "Schema/mysql";
}
```

```csharp
// Once per test project: tests share a container, so they run one at a time, and the
// containers are removed as soon as the run ends.
[assembly: DoNotParallelize]

[TestClass]
public static class TestRun
{
    [AssemblyCleanup]
    public static Task Cleanup() => DbTestRun.DisposeAllAsync();
}
```

## Why

A database test suite has to solve the same handful of problems, and most teams solve them
by hand:

| Problem | What `db-test` does |
|---|---|
| Where does the test database come from? | A throwaway container per engine, started on first use and shared by the run. |
| How does the schema get there? | Schema files on disk, applied in order, with each engine's own splitting rules. |
| A test dropped a column and the next test failed on it | The **shape guard** fails the test that did it, naming the object and the change. |
| One test's rows leaked into the next | `CleanAsync()` empties every table whatever the foreign keys, and resets identity counters. |
| "The suite is slow" | Per-test timings in a TSV file (`DBTEST_TIMING=1`). |
| The same assertions written three times | The engine sits behind one abstract class. |

## Install

```bash
dotnet add package DbTest.MSTest        # base class for MSTest (brings DbTest.Core)
dotnet add package DbTest.MsSql         # SQL Server engine
dotnet add package DbTest.PostgreSql    # PostgreSQL engine
dotnet add package DbTest.MySql         # MySQL engine
dotnet add package DbTest.MsSql.Dacpac  # deploy a .dacpac (SQL Server database projects)
```

`DbTest.Core` alone gives the harness without any test framework. The CLI:

```bash
dotnet tool install --global DbTest.Cli
```

## Requirements

- **Docker** must be running. The harness starts real containers through
  [Testcontainers](https://github.com/testcontainers/testcontainers-dotnet).
- **.NET 8 or newer.** The packages ship a `net8.0` and a `net10.0` asset: .NET 8 and 9
  resolve the `net8.0` one, .NET 10 and later the `net10.0` one. The CLI rolls forward, so
  it runs on whichever of those runtimes is installed.

## The schema

Point the harness at a directory of SQL files — through `SchemaRoot` in the base class, or
directly:

```csharp
await using var harness = await DbTestHarness.StartAsync(
    new PostgreSqlEngine(),
    schemaRoot: "Schema/postgres");
```

Copy the files next to the test binaries so a relative path works:

```xml
<None Include="Schema\**\*" CopyToOutputDirectory="PreserveNewest" />
```

Files are discovered recursively, ordered by their relative path (`001_tables.sql` before
`002_procedures.sql` — use leading zeros), and applied one after another. Each engine applies
them its own way:

| | SQL Server | PostgreSQL | MySQL |
|---|---|---|---|
| File format | `.sql` | `.sql` / `.psql` | `.sql` |
| Batch separator | `GO` (a client directive the server rejects), with `GO n` repeat counts | none | `DELIMITER` |
| Function body | `CREATE PROCEDURE` | `$$ … $$` | `BEGIN … END` |
| Applied as | one batch per `GO` segment | the whole file as one command | one statement per delimiter |

The splitters are real scanners: a `GO` or a delimiter inside a string literal, a quoted
identifier or a comment is never a separator, and a column named `delimiter_char` is not a
`DELIMITER` directive.

Excluded files are **reported**, never silently dropped:

```csharp
var load = SchemaLoader.Load("Schema/postgres", new SchemaLoadOptions
{
    ExcludedDirectories = ["maintenance", "scripts"],
    ExcludedFileNamePatterns = ["^create_", "^fix_"],
    OrderedDirectories = ["tables", "procedures"],   // each with its subdirectories, first
});

foreach (var exclusion in load.Excluded)
{
    Console.WriteLine($"{exclusion.RelativePath} skipped: {exclusion.Reason}");
}
```

A schema that only partly applied fails loudly at setup rather than surfacing later as
unrelated test failures: `StartAsync` throws a `SchemaApplyException` listing every broken
file.

## SQL Server database projects

A SQL Server database project (SSDT, or the SDK-style `Microsoft.Build.Sql`) keeps one object
per file, and the objects depend on each other across folders in both directions: a table in
`dbo/Tables` has a computed column calling a function in `dbo/Functions`, which reads another
table in `dbo/Tables`. No file order — not even `OrderedDirectories` — applies that. The
project builds into a `.dacpac`, and `DbTest.MsSql.Dacpac` deploys the dacpac with DacFx, in
dependency order:

```csharp
using DbTest.Core.Engine;
using DbTest.Core.Schema;
using DbTest.MsSql;
using DbTest.MsSql.Dacpac;
using DbTest.MSTest;

[TestClass]
public sealed class ShopTests : DbTestBase
{
    protected override DbEngine CreateEngine() => new MsSqlEngine();

    protected override SchemaSource? SchemaSource =>
        new DacpacSchemaSource(Path.Join(AppContext.BaseDirectory, "Shop.Database.dacpac"));
}
```

Reference the database project from the test project, so `dotnet test` builds the dacpac,
and copy the dacpac next to the test binaries — with every dacpac beside it, because the
dacpacs it references (`master.dacpac` from `Microsoft.SqlServer.Dacpacs.Master`, another
project's) are resolved from the folder it is deployed from:

```xml
<ItemGroup>
  <ProjectReference Include="..\Shop.Database\Shop.Database.sqlproj"
                    ReferenceOutputAssembly="false"
                    SkipGetTargetFrameworkProperties="true"
                    Private="false" />
</ItemGroup>

<Target Name="CopyDacpacs" AfterTargets="Build">
  <MSBuild Projects="@(ProjectReference)"
           Condition="'%(Extension)' == '.sqlproj'"
           Targets="GetTargetPath"
           BuildInParallel="false">
    <Output TaskParameter="TargetOutputs" ItemName="_DatabaseProjectDacpac" />
  </MSBuild>
  <ItemGroup>
    <_DacpacToCopy Include="%(_DatabaseProjectDacpac.RootDir)%(_DatabaseProjectDacpac.Directory)*.dacpac" />
  </ItemGroup>
  <Copy SourceFiles="@(_DacpacToCopy)" DestinationFolder="$(OutDir)" SkipUnchangedFiles="true" />
</Target>
```

[`samples/MsSqlDacpac`](samples/MsSqlDacpac) is a complete, runnable example: a database
project, its tests, and the build wiring. CI runs it on every change.

What to know:

- **A missing dacpac fails before any container starts**, with the full path it looked for.
  A dacpac SQL Server rejects fails setup with a `SchemaApplyException` naming the dacpac
  and the server's error.
- **Deploy options.** Without options, nothing is dropped, possible data loss does not block
  the deploy, database-level settings (recovery model, filegroups, …) are skipped because a
  throwaway container has no use for them, and every command is bounded by the engine's
  command timeout. Options you pass are used exactly as given, and a bare
  `new DacDeployOptions()` has no timeout at all — so start from the defaults:

  ```csharp
  var options = DacpacSchemaSource.CreateDefaultOptions(new MsSqlEngine());
  options.SqlCommandVariableValues["Environment"] = "Test";
  return new DacpacSchemaSource(path, options);
  ```

  Classes whose dacpac or options differ get separate containers.
- **`CleanAsync` also empties reference data** that the project's post-deployment script
  seeded. Run the script again after cleaning — the sample does it in `[TestInitialize]`,
  which works as long as the script is idempotent and has no SQLCMD syntax.
- **Telemetry.** DacFx sends usage telemetry to Microsoft unless the environment variable
  `DACFX_TELEMETRY_OPTOUT` is `1`; set `<DacFxTelemetryEnabled>false</DacFxTelemetryEnabled>`
  in the `.sqlproj` to switch it off at build time too. This repository does both.
- **Pin the SDK.** Put the `Microsoft.Build.Sql` version in `global.json` under
  `msbuild-sdks` and write `<Project Sdk="Microsoft.Build.Sql">` without a version, so every
  project builds with the same one.
- **Relative paths** given to a schema source resolve against the current directory; use
  `AppContext.BaseDirectory` for files copied next to the tests, as above. (`SchemaRoot`
  keeps resolving against the test binaries' directory.)

## The shape guard

A shared container means a shared schema. A test that drops or re-types a table leaves the
next test failing for the wrong reason — and the failure points at the victim, not the
culprit.

The guard snapshots every table, view and routine after the schema is applied. `DbTestBase`
re-reads them after every test and fails the test that changed one:

```
Assert.Fail failed. The test left a shared database object reshaped. Restore it before the
test ends (the shape guard compares against the schema as applied):
[ShapeGuard] CustomerTests.Adds_A_Column left dbo.customers columns changed (id,email) -> (id,email,probe)
```

The culprit is blamed once; the tests after it are not. An object that is put back is
forgotten, so the next test to break it is blamed again. Objects a test creates are not
watched — only the schema as applied.

It detects and blames. It does not repair: a silent repair hides the leak from the next run,
and the next run is exactly when you want to see it. Override `FailOnShapeLeak` to only log
the leak. Without the base class:

```csharp
var leaks = await harness.CheckShapeAsync("CustomerTests.Adds_A_Column");
```

## Cleanup

```csharp
await harness.CleanAsync();
```

Empties every user table, whatever the foreign keys between them, and puts every identity
column back to its seed, so the next row gets the same id on every engine. (MySQL keeps no
seed apart from the counter: its tables restart at 1.)

## Queries

```csharp
var count = await harness.QueryScalarAsync<int>("SELECT COUNT(*) FROM orders");
var newest = await harness.QueryScalarAsync<DateTimeOffset?>("SELECT MAX(placed_at) FROM orders");
```

`QueryScalarAsync<T>` reads the first column of the first row; SQL NULL or no row gives
`default`. It absorbs the drivers' differences: `COUNT(*)` is `int` on SQL Server and `bigint`
elsewhere, MySQL returns a `CHAR(36)` where SQL Server returns a `uniqueidentifier`, and
nullable targets work. For anything richer, open a connection:

```csharp
await using var connection = await harness.OpenConnectionAsync();
```

## Diagnostics

```bash
DBTEST_TIMING=1 dotnet test
```

With `DbTest.MSTest`, writes one row per test to `dbtest-timing.tsv` next to the test
binaries (or to `DBTEST_TIMING_FILE`). Off by default; no file is written unless it is
switched on.

## The CLI

```bash
dbtest engines                                         # list the registered engines
dbtest up --engine postgres --schema Schema/postgres   # start, apply, keep running until Ctrl+C
dbtest schema --engine mysql --schema Schema/mysql     # apply a schema and list what it created
```

## Layout

```
src/DbTest.Core          engine-agnostic: the DbEngine base class, schema loading, statement
                         splitting, the harness, the shape guard, diagnostics
src/DbTest.MSTest        DbTestBase and DbTestRun for MSTest
src/DbTest.MsSql         SQL Server engine
src/DbTest.MsSql.Dacpac  deploys a SQL Server .dacpac through DacFx (the only DacFx dependency)
src/DbTest.PostgreSql    PostgreSQL engine
src/DbTest.MySql         MySQL engine
src/DbTest.Cli           the `dbtest` command
tests/DbTest.Core.Tests  unit tests, no Docker
tests/DbTest.Integration.Tests
                         one shared test body, run against all three engines, plus a
                         dacpac deploy on SQL Server
samples/MsSqlDacpac      a SQL Server database project and its tests (own solution)
```

## Writing an engine

Derive from `DbEngine` and implement seven members: the name, the default image, a connection
factory, container startup, object listing, cleaning, and splitting one schema file into
commands. Applying a schema, running statements, binding parameters and converting scalars
are inherited, as are schema discovery, ordering, exclusion and the shape guard.

`DbEngine` is an abstract class rather than an interface so new capabilities can arrive as
virtual members with a default, without breaking an engine written outside this repository.

## Writing a schema source

A schema that is not a directory of SQL files comes in through a `SchemaSource`, passed to
`DbTestHarness.StartAsync(engine, source)` or returned from `DbTestBase.SchemaSource`. Implement
three members:

- `Key` identifies the schema: sources with equal keys share a container, so it must cover
  everything that changes what is deployed. It is read before any container starts, so it
  must not touch the file system or throw.
- `Validate(engine)` runs before the container starts. Throw `DbTestException` for a
  configuration mistake, such as a missing file, so it fails in milliseconds.
- `ApplyAsync(context)` puts the schema into `context.Database` and returns failures in the
  `SchemaApplyResult` rather than throwing them. `context.ConnectionString` holds the
  password: keep it out of messages.

`DirectorySchemaSource` (the `schemaRoot` behaviour) and `DacpacSchemaSource` are the two
built in.

## Maintainers

`db-test` is maintained by [dbaitest](https://github.com/dbaitest) and
[locd2o](https://github.com/locd2o).

## Contributing

Every change goes through a pull request. See [CONTRIBUTING.md](CONTRIBUTING.md) for the
workflow, the checks that run, and the merge policy. Security issues: see
[SECURITY.md](SECURITY.md). Changes per release: [CHANGELOG.md](CHANGELOG.md).

## License

MIT. See [LICENSE](LICENSE).
