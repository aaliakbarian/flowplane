#!/bin/sh

set -u

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
workflow="$repository_root/.github/workflows/foundation.yml"
readme="$repository_root/README.md"
contributing="$repository_root/CONTRIBUTING.md"
aggregate="$repository_root/tests/container/milestone0.sh"
migration_script="$repository_root/deploy/compose/apply-migrations.sh"
milestone_plan="$repository_root/docs/development/milestones/milestone-a-foundation-and-ssis-discovery.md"
failures=0

fail() {
  echo "foundation-contract: $*" >&2
  failures=$((failures + 1))
}

require_file() {
  test -f "$repository_root/$1" || fail "missing $1"
}

require_text() {
  file=$1
  text=$2
  grep -Fq -- "$text" "$file" || fail "$file is missing: $text"
}

reject_text() {
  file=$1
  text=$2
  if grep -Fq -- "$text" "$file"
  then
    fail "$file must not contain: $text"
  fi
}

require_file README.md
require_file CONTRIBUTING.md
require_file .github/workflows/foundation.yml
require_file tests/container/milestone0.sh
require_file deploy/compose/apply-migrations.sh

if test -f "$workflow"
then
  require_text "$workflow" 'runs-on: ubuntu-24.04'
  reject_text "$workflow" 'ubuntu-latest'
  require_text "$workflow" 'contents: read'
  reject_text "$workflow" 'contents: write'
  reject_text "$workflow" 'pull_request_target:'
  reject_text "$workflow" 'workflow_dispatch:'
  reject_text "$workflow" 'schedule:'
  require_text "$workflow" 'actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1'
  require_text "$workflow" 'actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0'
  require_text "$workflow" 'global-json-file: global.json'
  require_text "$workflow" 'actions/setup-node@949feb2413d6458794dcd2491c4babbbce0c15c1 # v7.1.0'
  require_text "$workflow" 'node-version: 24.21.0'
  require_text "$workflow" 'cache: npm'
  require_text "$workflow" 'cache-dependency-path: src/frontend/package-lock.json'
  require_text "$workflow" 'dotnet format FlowPlane.slnx --verify-no-changes --no-restore'
  require_text "$workflow" 'git diff --check "$empty_tree" HEAD'
  require_text "$workflow" "find tests deploy/compose -type f -name '*.sh' -exec sh -n {} \\;"
  expected_policy=$(printf '%s\n' \
    'on:' \
    '  push:' \
    '    branches:' \
    '      - main' \
    '  pull_request:' \
    '    branches:' \
    '      - main' \
    '' \
    'permissions:' \
    '  contents: read' \
    '' \
    'jobs:')
  actual_policy=$(sed -n '3,14p' "$workflow")
  test "$actual_policy" = "$expected_policy" ||
    fail "$workflow must contain exactly the approved triggers and permissions"
  test "$(grep -c '^    runs-on: ubuntu-24.04$' "$workflow")" -eq 5 ||
    fail "$workflow must run exactly five jobs on ubuntu-24.04"
  test "$(grep -c '^    branches:$' "$workflow")" -eq 2 ||
    fail "$workflow must define only the push and pull-request branch filters"
  test "$(grep -c '^      - main$' "$workflow")" -eq 2 ||
    fail "$workflow push and pull-request branch filters must both target main"
  expected_jobs=$(printf '%s\n' backend frontend containers postgresql documentation)
  actual_jobs=$(sed -n '/^jobs:/,$ s/^  \([a-z0-9_-]*\):$/\1/p' "$workflow")
  test "$actual_jobs" = "$expected_jobs" || fail "$workflow must contain exactly the five approved jobs"
  reject_text "$workflow" 'needs:'
  uses_count=$(grep -c '^[[:space:]]*uses:' "$workflow")
  immutable_uses_count=$(grep -Ec '^[[:space:]]*uses: [^[:space:]]+@[0-9a-f]{40}([[:space:]]+#.*)?$' "$workflow")
  test "$uses_count" -eq 8 || fail "$workflow must contain exactly eight approved action uses"
  test "$immutable_uses_count" -eq "$uses_count" || fail "$workflow action references must all use full commit SHAs"
  test "$(grep -Fc 'uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1' "$workflow")" -eq 5 ||
    fail "$workflow must use the approved checkout action exactly five times"
  test "$(grep -Fc 'uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0' "$workflow")" -eq 2 ||
    fail "$workflow must use the approved setup-dotnet action exactly twice"
  test "$(grep -Fc 'uses: actions/setup-node@949feb2413d6458794dcd2491c4babbbce0c15c1 # v7.1.0' "$workflow")" -eq 1 ||
    fail "$workflow must use the approved setup-node action exactly once"
  if grep -Eiq 'cache[^[:space:]]*:[[:space:]]*.*node_modules|path:[[:space:]]*.*node_modules' "$workflow"
  then
    fail "$workflow must not cache node_modules"
  fi
  reject_text "$workflow" 'Microsoft.Data.SqlClient'
  reject_text "$workflow" 'pull_request_target'
  reject_text "$workflow" 'permissions: write-all'
  if grep -Eq 'uses: [^ ]+@v[0-9]' "$workflow"
  then
    fail "$workflow contains a mutable major-version action reference"
  fi
  if grep -Eiq '(^|[^[:alpha:]])ssis([^[:alpha:]]|$)' "$workflow"
  then
    fail "$workflow must not run or configure SSIS"
  fi
fi

for heading in \
  '## Status' \
  '## What FlowPlane is' \
  '## Current MVP direction' \
  '## Architecture' \
  '## Development quick start' \
  '## Production-style frontend verification' \
  '## Repository layout' \
  '## Documentation' \
  '## Contributing' \
  '## License'
do
  require_text "$readme" "$heading"
done

reject_text "$readme" '. deploy/compose/.env'
require_text "$readme" 'sh deploy/compose/apply-migrations.sh'
reject_text "$repository_root/deploy/compose/README.md" '. deploy/compose/.env'
require_text "$repository_root/deploy/compose/README.md" 'sh deploy/compose/apply-migrations.sh'

if test -f "$migration_script"
then
  reject_text "$migration_script" 'eval '
  reject_text "$migration_script" '. "$'
  require_text "$migration_script" 'docker compose'
  require_text "$migration_script" 'config --environment'
  require_text "$migration_script" 'dotnet tool run dotnet-ef database update'
fi

require_milestone_task_state() {
  task_id=$1
  expected_status=$2
  expected_checklist_state=$3

  actual_status=$(awk -v task="$task_id" '
    $0 ~ ("^## " task " ") { in_task = 1; next }
    in_task && /^## A-[0-9][0-9] / { exit }
    in_task && /^\*\*Status:\*\*/ { print; exit }
  ' "$milestone_plan")
  expected_status_line="**Status:** $expected_status"
  if test "$actual_status" != "$expected_status_line"
  then
    fail "$milestone_plan must record $task_id as $expected_status; found: ${actual_status:-missing}"
  fi

  actual_checklist_state=$(awk -v task="$task_id" '
    /^## Progress tracking$/ { in_progress = 1; next }
    in_progress && /^## / { exit }
    in_progress && index($0, "- [x] " task " ") == 1 { print "checked" }
    in_progress && index($0, "- [ ] " task " ") == 1 { print "unchecked" }
  ' "$milestone_plan")
  if test "$actual_checklist_state" != "$expected_checklist_state"
  then
    fail "$milestone_plan must mark $task_id $expected_checklist_state in the progress checklist; found: ${actual_checklist_state:-missing}"
  fi
}

for task_id in A-01 A-02 A-03 A-04 A-05 A-06 A-07 A-08 A-09
do
  require_milestone_task_state "$task_id" Complete checked
done

for task_id in A-10 A-11 A-12
do
  require_milestone_task_state "$task_id" Planned unchecked
done

for command in \
  'dotnet restore FlowPlane.slnx --disable-parallel -m:1 --nologo' \
  'dotnet build FlowPlane.slnx --no-restore --nologo -m:1' \
  'dotnet test FlowPlane.slnx --no-restore --nologo -m:1' \
  'docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml up --build --wait' \
  'docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml down' \
  'sh tests/container/a07-production-frontend.sh'
do
  require_text "$readme" "$command"
done

require_text "$contributing" 'dotnet format FlowPlane.slnx --verify-no-changes --no-restore'

for target in \
  docs/README.md \
  docs/product/mvp-scope.md \
  docs/architecture/overview.md \
  docs/domain/mvp-domain-model.md \
  docs/architecture/providers/ssis-provider-technical-design.md \
  docs/development/mvp-implementation-plan.md \
  docs/development/milestones/milestone-a-foundation-and-ssis-discovery.md \
  docs/adr/README.md \
  CONTRIBUTING.md
do
  require_file "$target"
  require_text "$readme" "]($target)"
done

for heading in \
  '## Supported development environment' \
  '## Contributor setup' \
  '## Verification tiers' \
  '## Pull requests' \
  '## SSIS integration work'
do
  require_text "$contributing" "$heading"
done

if test -f "$aggregate"
then
  for contract in \
    a04-development-stack.sh \
    a05-postgresql-baseline.sh \
    a06-oidc-path.sh \
    a07-production-frontend.sh
  do
    require_text "$aggregate" "$contract"
  done
fi

if test "$failures" -ne 0
then
  echo "foundation-contract: $failures failure(s)" >&2
  exit 1
fi

echo 'foundation-contract: passed'
