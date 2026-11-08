# Changelog

All notable changes to the DbTest packages are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/) — while the major version is 0, a minor release
may break the API, and the breaking changes are listed first.

## [Unreleased]

### Changed

- The package project URL points to the documentation site, https://dbaitest.com/db-test/,
  instead of the GitHub repository. nuget.org shows it from the next release on. The README
  links the site too.

## [0.3.0] - 2026-11-08

### Added

- **`DbTest.MsSql.Dacpac`**, a new package: `DacpacSchemaSource` deploys a SQL Server `.dacpac`
  — the output of an SSDT or `Microsoft.Build.Sql` database project — into the container
  with DacFx, so a database project can be tested without ordering its files by hand. A
  missing dacpac fails before any container starts; a deploy SQL Server rejects fails setup
  with a `SchemaApplyException`, the password removed from the message. It depends on
  `Microsoft.SqlServer.DacFx` 170.5.96 and, through `DbTest.MsSql`, on the same
  `Microsoft.Data.SqlClient` 7.1.1, `Testcontainers.MsSql` and `System.Security.Cryptography.Pkcs`
  pins. DacFx declares SqlClient 6.x; the 7.x pairing is proven by a deploy test on net8.0
  and net10.0.
- `SchemaSource`, the extension point for a schema that is not a directory of SQL files:
  `Key` (shares containers), `Validate` (runs before the container starts) and `ApplyAsync`.
  `DirectorySchemaSource` is the directory behaviour `schemaRoot` has always had, and
  `SchemaApplyContext` carries the engine, connection string and database to a source.
- `DbTestHarness.StartAsync(engine, SchemaSource, …)`, `DbTestRun.GetHarnessAsync(engine,
  SchemaSource, …)` and `DbTestBase.SchemaSource`. Setting `SchemaSource` together with
  `SchemaRoot` or `SchemaLoadOptions` throws instead of ignoring one of them. A test class that
  already declares its own member named `SchemaSource` now hides the base member (CS0108,
  an error under `TreatWarningsAsErrors`): rename it.
- `samples/MsSqlDacpac`: a database project and its tests, run by CI.

### Changed

- Passing a `null` or `default` literal as the second argument of `DbTestHarness.StartAsync`
  or `DbTestRun.GetHarnessAsync` no longer compiles (CS0121: the call matches both the
  `string` and the `SchemaSource` overload). Omit the argument, or write `(string?)null`.
  Compiled code is unaffected.
- The release workflow pushes only `DbTest.*` packages, and CI and the release both fail
  when the packed set differs from the projects under `src/`.
- The README no longer shows a Sponsors badge or section: the GitHub Sponsors listing is
  not approved yet, and the badge rendered as "404: badge not found" on GitHub and nuget.org.

## [0.2.0] - 2026-11-08

### Breaking

- `IDbEngine` is replaced by the abstract class `DbEngine`. An engine implements seven
  members (`Name`, `DefaultImage`, `CreateConnection`, `StartContainerAsync`,
  `ListObjectsAsync`, `CleanAsync`, `SplitScript`) and inherits schema application, statement
  execution, parameter binding and scalar conversion. New capabilities can now be added
  without breaking engines written elsewhere.
- `QuerySingleAsync<T>` is renamed `QueryScalarAsync<T>` on the harness and on the engines.
  It always read the first column of the first row; the new name says so.
- `DbTestHarness.OpenConnection()` is renamed `CreateConnection()`: it never opened the
  connection. `OpenConnectionAsync()` still opens one.
- `SqlStatementSplitter.Split` and `SqlSplitOptions` are removed. Nothing used them: the
  PostgreSQL engine sends each file whole.
- `SchemaShapeGuard.CheckAsync` returns only the leaks it detects for the first time, and
  `SchemaShapeLeak.ResponsibleClass` is renamed `Responsible` (it now usually names a test).
- The packages target `net8.0` and `net10.0`. The `net9.0` asset is gone; .NET 9 consumers
  resolve the `net8.0` one.
- `DbTest.Core` no longer depends on Dapper, which it never used.
- `DbTest.PostgreSql` depends on Npgsql 10 (was 8). Npgsql 10 ships `net8.0` and `net10.0`
  assets, so both targets use the same version; a test project that references Npgsql 8 or 9
  directly moves to 10 with it.

### Added

- `DbTest.MSTest`: `DbTestBase` gives each test the run's shared harness for its engine, runs
  the shape guard after every test and fails the test that left a shared object reshaped, and
  records per-test timings when `DBTEST_TIMING=1`. `DbTestRun` shares one container per
  engine and schema across the run.
- `DbTestHarness.ListObjectsAsync()`.
- `GO n` repeat counts, indented and lowercase `GO`, and nested block comments in SQL Server
  scripts.
- The packages ship XML documentation (IntelliSense), Source Link and `.snupkg` symbols.

### Fixed

- SQL Server `CleanAsync` gave a table that had never held a row a first id of 0 instead of
  1, and ignored identity seeds other than 1. Tables no foreign key references are now
  truncated (an exact reset for any seed and type); referenced tables are deleted and
  reseeded to their seed, with an error naming the table when the column type cannot hold
  the reseed value.
- The SQL Server connection string assumed a local Docker host (`127.0.0.1`) and broke on a
  password containing `;`. It now comes from Testcontainers and `SqlConnectionStringBuilder`.
- The MySQL splitter treated a line starting with `delimiter…` (for example a column named
  `delimiter_char`) as a `DELIMITER` directive, and split on a delimiter inside a comment or a
  multi-line string. It is now a scanner that follows MySQL's lexical rules, and keeps
  executable comments (`/*!40101 … */`, as mysqldump writes them) and optimizer hints as code.
- `QueryScalarAsync<int?>` (and every nullable target) threw `InvalidCastException`. Guids
  from text, `DateTimeOffset` from `DateTime` and enums now convert too.
- `dbtest up` removed the container as soon as it printed its connection string. It now
  keeps it running until Ctrl+C.
- `dbtest` ignored unknown options and crashed on an option without a value; both are now
  usage errors.
- `TestDiagnostics.Flush` wrote every row again on each call and repeated the header.
  `DBTEST_TIMING` did nothing at all: nothing called `TestDiagnostics`. `DbTest.MSTest` wires it.
- PostgreSQL materialized views were reported as routines, and overloaded functions could
  produce a different argument order on each read, which the shape guard reported as a leak.
- `DbContainerOptions.StartupTimeout` was ignored. Container startup now honours it.
- Containers that failed to start were left for the resource reaper; they are now disposed.
- `SchemaLoadOptions.OrderedDirectories` ignored files in subdirectories of a listed directory.

### Security

- Testcontainers 4.16.0 brings SSH.NET 2026.0.0 (GHSA-mggc-4xg6-vcxf, GHSA-q939-rpr3-3284,
  both high).
- `System.Security.Cryptography.Pkcs`, pulled in by Microsoft.Data.SqlClient, is pinned to
  8.0.2 / 9.0.22 (GHSA-846f-f764-76mx high, GHSA-2fmv-rv2c-59x7 moderate).
- The build fails on any dependency with a moderate or worse advisory (NuGetAudit).

## [0.1.0] - 2026-11-07

First release: `DbTest.Core`, the SQL Server, PostgreSQL and MySQL engines, and the `dbtest`
CLI.

[Unreleased]: https://github.com/dbaitest/db-test/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/dbaitest/db-test/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/dbaitest/db-test/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/dbaitest/db-test/releases/tag/v0.1.0
