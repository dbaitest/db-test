## What changed

<!-- One or two sentences. What does this PR do, and why? -->

## Why

<!-- The problem this solves. Link the issue if there is one: "Fixes #123" -->

## How it was verified

<!--
What did you run, and what did you see? Be specific - a test name, a command, a
before/after. "Tests pass" is not evidence on its own; say what proves the change
does what it claims.

A change to a database engine needs its own proof on every engine it touches:
    dotnet test --filter "FullyQualifiedName~MsSql"
    dotnet test --filter "FullyQualifiedName~PostgreSql"
    dotnet test --filter "FullyQualifiedName~MySql"
-->

## Checklist

- [ ] The change is covered by a test that fails without it
- [ ] `dotnet build` succeeds
- [ ] `dotnet test` passes on every affected engine
- [ ] A behaviour change to a schema or an engine came with its test in the same commit
- [ ] No engine-specific detail leaked into a shared test body
- [ ] Public API changes are reflected in the README
- [ ] A user-facing change has a docs PR in [dbaitest/dbaitest.com](https://github.com/dbaitest/dbaitest.com) (link it here), or needs none

<!--
Engine-specific detail in a shared test body is the one thing worth checking by
hand. Raw DDL is allowed to differ per engine - the dialects genuinely differ - but
the assertions stay shared. If a shared assertion had to change to make an engine
pass, say so here and explain why.
-->
