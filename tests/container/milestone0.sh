#!/bin/sh

set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)

for contract in \
  a04-development-stack.sh \
  a05-postgresql-baseline.sh \
  a06-oidc-path.sh \
  a07-production-frontend.sh
do
  echo "Milestone 0: running $contract"
  if ! sh "$repository_root/tests/container/$contract"
  then
    echo "Milestone 0: $contract failed; remaining contracts were not run" >&2
    exit 1
  fi
done

echo 'Milestone 0 aggregate verification passed'
