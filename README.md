# db-test

A .NET library for **database integration testing** that runs the same test body against
**SQL Server, PostgreSQL and MySQL**.

Every run gets a throwaway database container. Nothing touches a shared, staging or
production database.

```csharp
public abstract class OrderTests : EngineTestBase
{
    protected override string EngineName => "postgres";

    [TestMethod]
    public async Task Total_Should_Sum_The_Customer_Orders()
    {
        await Harness.ExecuteAsync(
            "INSERT INTO customers (email, display_name) VALUES (@email, @name)",
            [new DbParameterValue("@email", "ada@example.test"), new DbParameterValue("@name", "Ada")]);

        var total = await Harness.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM customers WHERE email = @email",
            [new DbParameterValue("@email", "ada@example.test")]);

        Assert.AreEqual(1, total);
    }
}

[TestClass]
public sealed class PostgresOrderTests : OrderTests { }
[TestClass]
public sealed class MsSqlOrderTests : OrderTests { }
[TestClass]
public sealed class MySqlOrderTests : OrderTests { }
```

The assertions are written once. No engine name appears in them.

## Why

A database test suite has to solve the same handful of problems, and most teams solve them
by hand:

| Problem | What `db-test` does |
|---|---|
| Where does the test database come from? | A throwaway container per engine, started and destroyed by the harness. |
| How does the schema get there? | Schema files on disk, applied in order, with each engine's own splitting rules. |
| A test dropped a table and the next test failed on a missing object | The **shape guard** names the class that changed the shape. |
| One test's rows leaked into the next | `Clean()` empties every table in foreign-key order and resets identity counters. |
| "The suite is slow" | Optional per-test timing output (`DBTEST_TIMING=1`). |
| The same assertions written three times | The engine sits behind an interface. |

## Install

```bash
dotnet add package DbTest.Core          # the harness
dotnet add package DbTest.MsSql         # SQL Server engine
dotnet add package DbTest.PostgreSql    # PostgreSQL engine
dotnet add package DbTest.MySql         # MySQL engine
```

Or the CLI:

```bash
dotnet tool install --global DbTest.Cli
```

## Requirements

- **Docker** must be running. The harness starts real containers through
  [Testcontainers](https://github.com/testcontainers/testcontainers-dotnet).
- **.NET 8 runtime** must be installed. The libraries target `net8.0`; a newer SDK builds
  them but the test host needs the matching runtime.

## The schema

Point the harness at a directory of SQL files:

```csharp
await using var harness = await DbTestHarness.StartAsync(
    new PostgreSqlEngine(),
    schemaRoot: "Schema/postgres");
```

Files are discovered recursively, ordered by path, and applied one after another. Each
engine applies them its own way:

| | SQL Server | PostgreSQL | MySQL |
|---|---|---|---|
| File format | `.sql` | `.sql` / `.psql` | `.sql` |
| Batch separator | `GO` (a client directive the server rejects) | none | `DELIMITER` |
| Function body | `CREATE PROCEDURE` | `$$ … $$` | `BEGIN … END` |
| Applied as | one batch per `GO` segment | one statement per file | one statement per delimiter segment |

The splitter tracks string literals, line and block comments, dollar-quoted bodies and
`DELIMITER` directives, so a semicolon inside any of them does not split a statement.

Excluded files are **reported**, never silently dropped:

```csharp
var load = SchemaLoader.Load("Schema/postgres", new SchemaLoadOptions
{
    ExcludedDirectories = ["maintenance", "scripts"],
    ExcludedFileNamePatterns = ["^create_", "^fix_"],
    OrderedDirectories = ["tables", "procedures"],
});

foreach (var exclusion in load.Excluded)
{
    Console.WriteLine($"{exclusion.RelativePath} skipped: {exclusion.Reason}");
}
```

A schema that only partly applied fails loudly at setup rather than surfacing later as
unrelated test failures:

```csharp
var applied = await engine.ApplySchemaAsync(connection, load.Scripts);
applied.ThrowIfFailed();   // throws SchemaApplyException listing every broken file
```

## The shape guard

A shared container means a shared schema. A test that drops or re-types a table leaves the
next test failing for the wrong reason — and the failure points at the victim, not the
culprit.

The guard snapshots every object after the schema is applied, re-reads it after each test
class, and names the class that changed the shape:

```
[ShapeGuard] OrderTests left dbo.customers columns changed (id,email) -> (id,email,probe)
```

It detects and blames. It does not repair: a silent repair hides the leak from the next run,
and the next run is exactly when you want to see it.

```csharp
var leaks = await harness.CheckShapeAsync("OrderTests");
```

## Cleanup

```csharp
await harness.CleanAsync();
```

Empties every user table in foreign-key order and resets identity counters. The container is
discarded afterwards, so this is about isolating tests from each other, not about hygiene.

## Diagnostics

```bash
DBTEST_TIMING=1 dotnet test
```

Writes one row per test to `dbtest-timing.tsv` next to the test binaries. Off by default; no
file is written and no query runs unless it is switched on.

## The CLI

```bash
dbtest engines                                  # list the registered engines
dbtest up --engine postgres --schema Schema     # start a container, apply a schema
dbtest schema --engine mysql --schema Schema    # apply a schema and list what it created
```

## Layout

```
src/DbTest.Core          engine-agnostic: schema loading, statement splitting,
                         the harness, the shape guard, diagnostics
src/DbTest.MsSql         SQL Server engine
src/DbTest.PostgreSql    PostgreSQL engine
src/DbTest.MySql         MySQL engine
src/DbTest.Cli           the `dbtest` command
tests/DbTest.Integration.Tests
                         one shared test body, run against all three engines
```

## Writing an engine

Implement `IDbEngine`. Six members: start a container, apply schema, list objects, clean,
execute a statement, run a query. Everything else — schema discovery, ordering, exclusion,
the shape guard — lives in the core and is shared.

## Contributing

Every change goes through a pull request. See [CONTRIBUTING.md](CONTRIBUTING.md) for the
workflow, the checks that run, and the merge policy.

## License

MIT. See [LICENSE](LICENSE).
