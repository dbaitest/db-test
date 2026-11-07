# Contributing

Every change to this repository goes through a pull request. Direct pushes to `main` are
rejected by GitHub, and a local hook rejects them before they leave your machine.

## The workflow

```bash
git switch -c feature/short-description
# ...make your change...
git add -A
git commit -m "feat(core): what you changed"
git push -u origin feature/short-description
gh pr create --base main --fill
```

Then wait for CI, get a review, and merge.

Enable the local hook once per clone so a direct push fails fast instead of at GitHub:

```bash
git config core.hooksPath .githooks
```

## Before you open the pull request

```bash
dotnet build
dotnet test --filter "FullyQualifiedName~Unit"           # fast, no container
dotnet test -f net8.0                                    # all three engines, one runtime
```

Docker must be running: the integration tests start real database containers.

Everything targets `net8.0`, `net9.0` and `net10.0`, so `dotnet build` builds three times
over and `dotnet test` runs the suite once per framework — pass `--framework` (or `-f`) to
pick one.

## What CI checks

| Check | What it enforces |
|---|---|
| `build` | Builds the solution for every supported framework and runs the unit tests on each |
| `integration (MsSql)` | The full behaviour suite against SQL Server, on .NET 8, 9 and 10 |
| `integration (PostgreSql)` | The full behaviour suite against PostgreSQL, on .NET 8, 9 and 10 |
| `integration (MySql)` | The full behaviour suite against MySQL, on .NET 8, 9 and 10 |
| `PR title and description` | The title follows Conventional Commits; the description is filled in |

All of them are required before a pull request can merge. The engine jobs run in parallel
and report separately, so a failure in one engine does not hide the others.

## Writing the pull request

The title must follow Conventional Commits:

```
type(scope): summary
```

`feat` `fix` `docs` `style` `refactor` `perf` `test` `build` `ci` `chore` `revert`

```
feat(core): add a shape guard for dropped tables
fix(mysql): reset identity counters after truncate
docs: record the engine traps
```

The description follows the template: what changed, why, and **how it was verified**. Name
the command you ran and what you saw — "tests pass" is not evidence.

## Merge policy

Squash merge only. The commit message comes from the pull request title, so write it for
the history: it will be the line people read in `git log`.

The branch is deleted automatically after the merge. Discussions must be resolved, and
reviews are dismissed when new commits land, so re-read the diff before approving.

**Review requirement.** A pull request needs one approving review from a code owner.
GitHub does not let you approve your own pull request, so with one maintainer that
requirement could never be satisfied — which is why `locd2o` was added as a second
maintainer and a code owner. In practice:

- A pull request from `locd2o` waits for `dbaitest` to approve it.
- A pull request from `dbaitest` waits for `locd2o` to approve it.

Either way, nobody merges their own work, and the rules apply to administrators too, so
neither maintainer can bypass the requirement.

The rest of the protection on `main` is not optional:

| Rule | Effect |
|---|---|
| Pull request required | A direct push is rejected. |
| The CI checks required | A merge waits for them, and waits again if a commit is pushed. |
| One code owner approval | The other maintainer reviews it before it lands. |
| Admins included | The rule applies to the maintainer too, so it cannot be bypassed. |
| No force push, no branch deletion | History cannot be rewritten and the branch cannot be dropped. |
| Linear history | No merge commits. |
| Conversations resolved | An open discussion blocks the merge. |

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

- Supported runtimes: `net8.0`, `net9.0` and `net10.0`. The list lives in
  `DbTestTargetFrameworks` in `Directory.Build.props`; every project targets all of them,
  so a change to that list changes what CI builds and runs.
- Building the repository needs the .NET 10 SDK — `global.json` pins it, because targeting
  `net10.0` needs an SDK that new. Consuming the packages needs only .NET 8 or newer.
- `Microsoft.Data.SqlClient` does **not** work under globalization invariant mode. Do not
  enable `InvariantGlobalization` in `Directory.Build.props`.
- `MySqlConnector` must stay at 2.6.1 or later (GHSA-473q-m89c-ghf8).
- `NuGet.config` clears inherited sources on purpose: this is a public repository and must
  not restore from private feeds.

## Commits

Conventional Commits (`feat:`, `fix:`, `docs:`, `chore:`, `test:`).

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
