# Security policy

## Supported versions

Only the latest release on nuget.org receives fixes. While the major version is 0 there are
no maintenance branches: a fix ships as the next release.

## Reporting a vulnerability

Do not open a public issue. Report it privately through GitHub:
**Security → Report a vulnerability** on this repository
(<https://github.com/dbaitest/db-test/security/advisories/new>).

Include what is affected (package and version), how to reproduce it, and what an attacker
gains. You will get an answer within seven days. Once a fix is released, the advisory is
published with credit to the reporter, unless you ask not to be named.

## Scope

DbTest is a test harness: it starts throwaway containers with default credentials and is
not meant to run against a production database. A report is in scope when DbTest itself —
or a dependency it ships — lets an attacker do something the test author did not intend,
for example execute code through a crafted schema file name or leak data from the host.
Weak default container passwords are by design and out of scope.

Dependencies are checked on every build: a dependency with a moderate or worse advisory
fails CI, and Dependabot proposes the update.

One gap: the `Microsoft.Build.Sql` MSBuild SDK, which builds the database project in
`samples/MsSqlDacpac`, ships its own copies of DacFx and SqlClient inside the SDK package.
NuGet's audit does not see an SDK's contents, so an advisory against those copies does not
fail CI. The SDK only runs at build time, only for the sample (the release workflow never
builds it), and its version is pinned in `global.json` under `msbuild-sdks`. The DacFx that
`DbTest.MsSql.Dacpac` ships with is an ordinary package reference and is audited.
