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
dotnet build                                                     # warnings are errors
dotnet test tests/DbTest.Core.Tests                              # unit tests, no Docker
dotnet test tests/DbTest.Integration.Tests -f net10.0            # all three engines, one runtime
```

Docker must be running for the integration tests: they start real database containers.

Everything targets `net8.0` and `net10.0`, so `dotnet build` builds twice over and
`dotnet test` runs a suite once per framework — pass `--framework` (or `-f`) to pick one.

The build is strict: every warning is an error, the `.editorconfig` style rules are enforced,
and NuGet's vulnerability audit fails the restore when any dependency, direct or transitive,
has a moderate or worse advisory.

## What CI checks

| Check | What it enforces | Required |
|---|---|---|
| `build` | Linux: builds every framework, runs the unit tests on each with coverage, packs (package validation) | yes |
| `build (windows)` | The same build and unit tests on Windows (CRLF, path separators) | no |
| `integration (MsSql)` | The full behaviour suite against SQL Server, on .NET 8 and 10 | yes |
| `integration (PostgreSql)` | The full behaviour suite against PostgreSQL, on .NET 8 and 10 | yes |
| `integration (MySql)` | The full behaviour suite against MySQL, on .NET 8 and 10 | yes |
| `PR title and description` | The title follows Conventional Commits; the description is filled in | no |
| `analyze (csharp)`, `analyze (actions)` | CodeQL security and quality queries | no |

The required checks block the merge. The engine jobs run in parallel and report separately,
so a failure in one engine does not hide the others. Workflow actions are pinned to a commit
SHA; Dependabot proposes the updates.

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

## Releasing

A release is a tag plus a GitHub release. The `Publish` workflow runs on the published
release, builds and unit-tests the tagged commit, and pushes every package (and its `.snupkg`
symbols) to nuget.org.

```bash
# The tag is the version: v0.2.0 publishes 0.2.0. No file in the repository holds a version;
# a local build is 0.0.0-dev.
gh release create v0.2.0 --notes-file <(sed -n '/^## \[0.2.0\]/,/^## \[/p' CHANGELOG.md | sed '$d')
```

Move the `Unreleased` section of [CHANGELOG.md](CHANGELOG.md) under the new version first.

The workflow publishes through **trusted publishing**: GitHub mints an OIDC token, nuget.org
checks it against the policy registered for this repository and hands back a key that lives
for one hour. There is no API key in the repository and nothing to rotate.

The policy on nuget.org (username → *Trusted Publishing*) is:

| Field | Value |
|---|---|
| Repository Owner | `dbaitest` |
| Repository | `db-test` |
| Workflow File | `publish.yml` |
| Environment | `production` |

The environment is why the publish job declares `environment: production` — the OIDC token
only carries that claim when the job names the environment. The environment exists in the
repository settings and carries no protection rules, so the publish is not blocked on a
reviewer.

A new policy starts in a **seven-day pending state** and becomes permanent after the first
successful publish. If seven days pass without one, the policy goes inactive and nuget.org
offers an *Activate for 7 days* button on the policy.

If a publish fails half way, run the `Publish` workflow by hand and give it the release tag.
The push uses `--skip-duplicate`, so the packages that already landed do not fail the run.

`PackageValidationBaselineVersion` in `src/Directory.Build.props` names the last release:
`dotnet pack` (and so the `build` check) fails when the public API breaks compatibility with
it. After each release, raise it to the new version; a deliberate breaking change raises it
in the same pull request. A package published for the first time has no baseline yet, so
release it once before its ID is validated.

## Ground rules

**The engine base class is the abstraction boundary.** If a test needs to know which engine it
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

1. Add `src/DbTest.<Name>` with a class deriving from `DbEngine`.
2. Add the engine to `EngineRegistration.RegisterBuiltins()` in the CLI.
3. Add a schema under `tests/DbTest.Integration.Tests/Schema/<name>/`, including the
   `customers`, `orders` and `audit_entries` tables and the `customer_order_total` routine.
4. Add a `[TestClass]` deriving from `HarnessBehaviourTestsBase` that overrides
   `CreateEngine`, `SchemaDirectory` and the two probe-column statements.
5. Add the engine to the `integration` matrix in `.github/workflows/ci.yml`.

The shared test body must pass unchanged. If it does not, the difference is either a bug in
the engine or a real dialect difference — in which case the dialect-specific part belongs in
the engine's own test class, and the shared assertion stays shared.

## Dependencies

- Supported runtimes: `net8.0` (the floor) and `net10.0`. .NET 9 consumers resolve the
  `net8.0` asset. The list lives in `DbTestTargetFrameworks` in `Directory.Build.props`;
  every project targets all of it, so a change to that list changes what CI builds and runs.
- Building the repository needs the .NET 10 SDK — `global.json` pins it, because targeting
  `net10.0` needs an SDK that new. Consuming the packages needs only .NET 8 or newer.
- `Microsoft.Data.SqlClient` does **not** work under globalization invariant mode. Do not
  enable `InvariantGlobalization` in `Directory.Build.props`.
- `MySqlConnector` must stay at 2.6.1 or later (GHSA-473q-m89c-ghf8), and the
  Testcontainers packages at 4.16.0 or later (they bring a patched SSH.NET).
- `System.Security.Cryptography.Pkcs` is pinned in `Directory.Packages.props` because every
  `Microsoft.Data.SqlClient` release still depends on a vulnerable version. The comment there
  says when the pin can go.
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
