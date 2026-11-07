# Contributing

## Getting set up

```bash
git clone https://github.com/dbaitest/db-test.git
cd db-test
dotnet restore
dotnet build
```

Docker must be running: the integration tests start real database containers.

```bash
dotnet test                                              # everything
dotnet test --filter "FullyQualifiedName~Unit"           # no container needed, fast
dotnet test --filter "FullyQualifiedName~PostgreSql"     # one engine
```

## Push access

Forks are welcome. If you would rather push straight to this repository, ask a maintainer
to grant you write access.

Whichever route you take, the credentials are yours to manage — this repository does not
carry anybody's keys or tokens. Two common setups:

**HTTPS with the GitHub CLI** (the token is stored in your system credential store):

```bash
gh auth login
git remote set-url origin https://github.com/dbaitest/db-test.git
gh auth setup-git
```

**SSH**, which does not expire and needs no browser step after the key is registered:

```bash
ssh-keygen -t ed25519 -C "you@example.com"
# add ~/.ssh/id_ed25519.pub at https://github.com/settings/ssh/new
git remote set-url origin git@github.com:dbaitest/db-test.git
```

## Ground rules

**The engine interface is the abstraction boundary.** If a test needs to know which engine it
is running on, the abstraction has a hole. Raw DDL is the one legitimate exception — the
three dialects genuinely differ — so dialect-specific DDL belongs in the engine's test
class, and the assertions stay in the shared base class.

**The shape guard detects, it does not repair.** A silent repair hides the leak from the
next run.

**A schema that only partly applied must fail at setup**, not surface later as unrelated
test failures.

**Exclusions are reported, never silent.** A file skipped by a naming rule is listed so the
caller can log or fail on it.

**Nothing touches a shared database.** Every run is a throwaway container.

## Adding an engine

1. Add `src/DbTest.<Name>` implementing `IDbEngine`.
2. Add the engine to `EngineRegistration.RegisterBuiltins()` in the CLI.
3. Add a schema under `tests/DbTest.Integration.Tests/Schema/<name>/`.
4. Add a test class deriving from `HarnessBehaviourTestsBase`.

The shared test body must pass unchanged. If it does not, the difference is either a bug in
the engine or a real dialect difference — in which case the dialect-specific part belongs in
the engine's own test class, and the shared assertion stays shared.

## Dependencies

- .NET 8.0 target. The test host needs a matching runtime installed.
- `Microsoft.Data.SqlClient` does **not** work under globalization invariant mode. Do not
  enable `InvariantGlobalization` in `Directory.Build.props`.
- `MySqlConnector` must stay at 2.6.1 or later (GHSA-473q-m89c-ghf8).
- `NuGet.config` clears inherited sources on purpose: this is a public repository and must
  not restore from private feeds.

## Commits

Conventional Commits (`feat:`, `fix:`, `docs:`, `chore:`, `test:`).
