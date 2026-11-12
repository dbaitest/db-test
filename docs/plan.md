# db-test — plan

The repository was empty when this started. This document records what the project is, what
was built, and what is deliberately not built yet.

## 1. What this project is

`db-test` is a .NET library and CLI for **database integration testing** that runs the same
test body against **SQL Server, PostgreSQL and MySQL**.

The unit of value is not "a way to connect to a database". It is the set of problems that
every database test suite hits and that most teams solve by hand, badly:

| Problem | What `db-test` does |
|---|---|
| Where does the test database come from? | A throwaway container per engine, started and destroyed by the harness. Nothing touches a shared or staging database. |
| How does the schema get there? | Schema files on disk, applied in order, with the engine's own splitting rules (`GO` batches, dollar-quoted bodies, `DELIMITER` directives). |
| A test dropped a table and the next test failed on a missing object | The **shape guard** snapshots every object after the schema is applied and fails the test that changed the shape. |
| One test's rows leaked into the next | `CleanAsync()` empties every table whatever the foreign keys and resets identity counters to their seed. |
| "The suite is slow" | Optional per-test timing output (`DBAITEST_TIMING=1`). |
| The same assertions written three times | The engine is behind one abstract class; the test body never mentions one. |

## 2. Architecture

```
src/DbAiTest.Core          engine-agnostic: the DbEngine base class, schema loading,
                           statement splitting, the harness, the shape guard, diagnostics
src/DbAiTest.MSTest        DbAiTestBase / DbAiTestRun: shared harness per engine, shape guard
                           after every test, timings
src/DbAiTest.MsSql         SQL Server engine
src/DbAiTest.PostgreSql    PostgreSQL engine
src/DbAiTest.MySql         MySQL engine
src/DbAiTest.Cli           the `dbaitest` command
tests/DbAiTest.Core.Tests  unit tests: splitters, scalar conversion, loader, guard,
                           diagnostics, CLI parsing — no Docker
tests/DbAiTest.Integration.Tests
                           one shared test body, built on DbAiTest.MSTest, run against all
                           three engines
```

### The engine contract

`DbEngine` (in `DbAiTest.Core`) is an abstract class. An engine implements only what is
genuinely engine-specific:

- its name and default image, and a connection factory;
- start a container and hand back a connection string;
- split one schema file into the commands the server accepts;
- list the objects the database holds (name, kind, columns);
- empty every table and reset identities.

Applying a schema (collecting every failing file), running statements, binding parameters
and converting scalars are inherited. Schema discovery, ordering, exclusion and the shape
guard live in the core and are identical across engines.

It is an abstract class rather than an interface so that a capability added later arrives as
a virtual member with a default, without breaking an engine written outside this repository.

This is the single most important decision in the project. The three engines disagree on
almost everything, and each disagreement used to be a special case in test code:

| Concern | SQL Server | PostgreSQL | MySQL |
|---|---|---|---|
| Batch separator | `GO` (client directive the server rejects) | none | `DELIMITER` directive |
| Function body | `CREATE PROCEDURE` body | `$$ ... $$` | `BEGIN ... END` |
| Empty a table | `DELETE` + `NOCHECK CONSTRAINT` | `TRUNCATE ... CASCADE` | `TRUNCATE` + `FOREIGN_KEY_CHECKS=0` |
| Introspect objects | `sys.objects` / `sys.columns` | `pg_class` / `pg_attribute` | `information_schema` |
| Login | always `sa`, database created by the harness | configurable | configurable |

### The shape guard

`SchemaShapeGuard` is the piece worth porting from the internal PMI harness. A shared
container means a shared schema; a test that drops or re-types a table leaves the next test
failing for the wrong reason. The guard snapshots every object after the schema is applied;
`DbAiTestBase` re-reads it after every test and fails the test that changed one:

```
[ShapeGuard] CustomerTests.Adds_A_Column left public.customers columns changed (id,email,...) -> (id,email,...,dbaitest_shape_probe)
```

Each damaged object is blamed once, on the first test that saw it damaged; an object put
back into shape is forgotten, so a later test that breaks it again is blamed again.

It detects and blames. It does not repair: a silent repair hides the leak from the next run,
and the next run is exactly when you want to see it.

## 3. What was built and verified

- `DbAiTest.Core` — the `DbEngine` base class, schema loader, statement splitters (`GO`
  batches, `DELIMITER`), scalar conversion, harness, shape guard, timing diagnostics, engine
  registry.
- `DbAiTest.MSTest` — `DbAiTestBase` and `DbAiTestRun`.
- `DbAiTest.MsSql`, `DbAiTest.PostgreSql`, `DbAiTest.MySql` — one engine each.
- `DbAiTest.Cli` — `dbaitest engines`, `dbaitest up`, `dbaitest schema`.
- Unit tests — splitters, scalar conversion, loader ordering and exclusion, shape guard,
  diagnostics, CLI parsing, registry.
- Integration tests — one shared test body asserting container startup, schema application,
  seeding, cleanup with identity reset (including a table that never held a row), nullable
  and cross-driver scalar conversion, foreign-key enforcement, and shape-guard detection,
  executed against all three engines through `DbAiTest.MSTest`.

**Verification (0.2.0): 154/154 tests pass in Release from a clean tree**, on Windows with
Docker Desktop 29.7.2 and .NET SDK 10.0.301: 53 unit tests and 24 integration tests (8
behaviour tests × 3 engines), each run on net8.0 and on net10.0. Two mutation checks back the
new tests: restoring the old `RESEED, 0` makes the never-used-table test fail with id 0, and
a test that leaves a column added fails in its own cleanup while the next test passes.

### Supported runtimes

The packages target `net8.0` (the floor) and `net10.0` (the primary target); both are LTS.
`DbAiTestTargetFrameworks` in `Directory.Build.props` holds that list, and every project — the
libraries, the CLI and the suites — targets all of it. A consumer on .NET 8 or 9 resolves the
`net8.0` asset; a consumer on .NET 10 or later resolves the `net10.0` one. The `net9.0`
target was dropped in 0.2.0: .NET 9 is an STS release, and its consumers lose nothing by
resolving `net8.0`.

Two mechanisms make "net8.0 or newer" true rather than nominal:

- `RollForward=Major` in the runtime configuration of the CLI and of the test hosts. A build
  for .NET 8 runs on .NET 9, 10 and later when the .NET 8 runtime is not installed; without
  it, a machine with only .NET 10 refuses to start the .NET 8 test host.
- The CI matrix runs the behaviour suite on each runtime, and the unit tests run once per
  target framework on every build.

Building the repository needs the .NET 10 SDK, which `global.json` pins: targeting
`net10.0` needs an SDK that new. Consuming the packages needs only .NET 8 or newer.

### Traps hit while building this, and what they teach

These are engine behaviours that cost real debugging time. Each one is now handled in the
core, and each is worth knowing before writing another engine.

| Trap | What happens | Fix |
|---|---|---|
| `sys.objects.type` is `char(2)` | Cast to string keeps the padding, so `"U "` never equals `"U"` and every table is classified as a routine | `RTRIM(CAST(o.type AS NVARCHAR(2)))` |
| `pg_class.relkind` is `char` | Npgsql refuses to read a `char` column as `System.String` | `relkind::text` |
| Routines live outside the relation catalog | `pg_class` has no procedures; `information_schema.tables` has no MySQL routines | A second query per engine (`pg_proc`, `information_schema.routines`) |
| `DBCC CHECKIDENT` takes a quoted, comma-separated name | `DBCC CHECKIDENT ([dbo].[t]) RESEED WITH 0` is a syntax error | `DBCC CHECKIDENT ('[dbo].[t]', RESEED, 0)` |
| `DBCC CHECKIDENT … RESEED, n` depends on history | The next id is `n + increment` if the table ever held a row, but `n` itself if it never did — so reseeding to 0 gave a never-used table a first id of 0 | Reseed to `seed` when `sys.identity_columns.last_value` is NULL, else to `seed − increment` |
| `delimiter` is not reserved in MySQL | A line-based `DELIMITER` match turned a column named `delimiter_char` into a directive | A scanner that only accepts the directive between statements |
| Testcontainers' and SqlClient's transitive dependencies | SSH.NET and System.Security.Cryptography.Pkcs shipped with high-severity advisories | NuGetAudit as an error; Testcontainers 4.16; a CPM pin for Pkcs |
| SQL Server's `sa` password policy | A simple password fails the container at startup, not at first login | The MSSQL engine owns a policy-compliant default |
| Globalization invariant mode | `Microsoft.Data.SqlClient` refuses to run under it | `InvariantGlobalization` is deliberately off, with a comment saying why |
| `SqlDataReader.GetChar` | Not supported by SqlClient | Cast in SQL instead |
| MySQL's `TRUNCATE` respects foreign keys | Needs `FOREIGN_KEY_CHECKS=0`, which must be in the same command as the truncates | The table list is read first, then truncated in one batch |


## 4. What is deliberately not built yet

These are the parts of the internal PMI harness that are valuable but are **not** generic
enough to ship without a design pass. They are listed as work, not as features.

### 4.1 Change-to-test selection (`-Changed`)

The internal harness maps a changed SQL file to the test classes that cover it, using the
schema's dependency graph. Doing this generically needs a schema parser per engine:

- SQL Server has DacFx, which gives a real model graph.
- PostgreSQL and MySQL do not ship one. The internal PG harness falls back to "any changed
  `.psql` runs the whole suite", which is safe but coarse.

Until a parser exists for all three, the honest generic behaviour is: **run everything, or
filter by test class name**. Shipping a partial implementation would produce false confidence
in the coverage claim, which is worse than not claiming it.

### 4.2 Coverage reporting

The internal tool reports each schema object in one of five tiers (`dedicated`, `reached`,
`named`, `manifest`, `shadowed`). The `reached` tier depends on the dependency graph from
4.1. The `dedicated` tier — "does a test class named after this object exist?" — is
engine-independent and can be built now; it is the natural next piece.

### 4.3 Caller tracing (blast radius)

The internal tool answers "who calls this stored procedure" from three sources: the schema
graph, a text scan for dynamic SQL, and a scan of the C# that executes SQL by name. The
first is engine-specific (see 4.1); the second and third are not, and could ship first as a
standalone `dbaitest trace-callers` command over a directory of `.sql` files.

### 4.4 Mutate-and-verify automation

The internal discipline — break the code deliberately, prove the test fails, restore — is
enforced by convention, not by tooling. Stryker.NET covers the C# half. A SQL half would need
a mutation operator per engine.

### 4.5 Seeded reference data as a first-class concept

The internal suites keep tripping over "the calendar table ships empty, so the procedure
returns nothing". `db-test` currently lets each test seed what it reads. A shared, per-engine
seed fixture (with the date dimension that keeps biting) is worth adding.

## 5. Design rules to keep

1. **The engine base class is the whole abstraction boundary.** If a test needs to know which
   engine it is on, the abstraction has a hole.
2. **The shape guard detects, it does not repair.** A silent repair hides the leak.
3. **A schema that only partly applied must fail loudly at setup**, not surface later as
   unrelated test failures. `SchemaApplyResult.ThrowIfFailed()` exists for this.
4. **Exclusions are reported, never silent.** A file skipped by a naming rule is listed by
   `SchemaLoader` so a caller can log or fail on it.
5. **Nothing touches a shared database.** Every run is a throwaway container.

## 6. Open questions for the maintainers

- Should the CLI grow a `test` command that runs a suite with a filter, or stay a
  container/schema utility that a test project calls?
- Should `db-test` support connecting to an existing database (a developer's local
  instance) as well as starting containers?
- Is per-engine schema layout (separate `schema/mssql`, `schema/postgres`, `schema/mysql`
  directories) the right model, or should one dialect-neutral schema be translated?
