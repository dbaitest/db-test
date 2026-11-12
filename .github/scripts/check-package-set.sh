#!/usr/bin/env bash
# Fails unless the packages in an output directory are exactly one per project under src/.
#
# Every project under src/ ships to nuget.org, and nothing else may: a sample or test project
# that became packable would otherwise be pushed by the release workflow, and a pushed
# version can be unlisted but never deleted.
#
# Usage: .github/scripts/check-package-set.sh [artifacts-dir]
set -euo pipefail

dir="${1:-artifacts}"

expected=$(for project in src/*/*.csproj; do basename "$project" .csproj; done | sort)

# DbAiTest.MsSql.Dacpac.0.3.0-dev.nupkg -> DbAiTest.MsSql.Dacpac (the id ends where the version starts).
actual=$(for package in "$dir"/*.nupkg; do
  basename "$package" .nupkg | sed -E 's/\.[0-9]+\.[0-9]+\.[0-9]+([-+].*)?$//'
done | sort)

if [ "$expected" != "$actual" ]; then
  echo "::error::The packed set does not match the projects under src/."
  diff <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") | sed 's/^/  /' || true
  echo "  (< expected from src/, > found in $dir)"
  exit 1
fi

echo "Packed exactly the src/ projects:"
printf '  %s\n' $actual
