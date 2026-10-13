#!/bin/sh

set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
compose_file="$repository_root/deploy/compose/compose.yaml"
environment_file=${1:-"$repository_root/deploy/compose/.env"}

if ! test -f "$environment_file"
then
  echo "Missing Compose environment file: $environment_file" >&2
  echo 'Copy deploy/compose/.env.example to deploy/compose/.env first.' >&2
  exit 1
fi

resolved_environment=$(
  docker compose \
    --env-file "$environment_file" \
    --file "$compose_file" \
    config --environment
)

read_compose_value() {
  variable_name=$1
  value=$(
    printf '%s\n' "$resolved_environment" |
      awk -v prefix="$variable_name=" '
        index($0, prefix) == 1 {
          print substr($0, length(prefix) + 1)
          found = 1
          exit
        }
        END { if (!found) exit 1 }
      '
  ) || {
    echo "Missing required Compose variable: $variable_name" >&2
    exit 1
  }
  printf '%s' "$value"
}

postgres_port=$(read_compose_value FLOWPLANE_POSTGRES_PORT)
postgres_database=$(read_compose_value FLOWPLANE_POSTGRES_DB)
postgres_username=$(read_compose_value FLOWPLANE_POSTGRES_USER)
postgres_password=$(read_compose_value FLOWPLANE_POSTGRES_PASSWORD)

cd "$repository_root"

dotnet tool restore
FlowPlane__PostgreSql__Host=127.0.0.1 \
FlowPlane__PostgreSql__Port="$postgres_port" \
FlowPlane__PostgreSql__Database="$postgres_database" \
FlowPlane__PostgreSql__Username="$postgres_username" \
FlowPlane__PostgreSql__Password="$postgres_password" \
  dotnet tool run dotnet-ef database update \
  --project src/backend/FlowPlane.Infrastructure/FlowPlane.Infrastructure.csproj \
  --startup-project src/backend/FlowPlane.Infrastructure/FlowPlane.Infrastructure.csproj
