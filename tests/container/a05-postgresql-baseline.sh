#!/bin/sh

set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
compose_file="$repository_root/deploy/compose/compose.yaml"
project_name="flowplane-a05-verification-$$"
log_file=$(mktemp /tmp/flowplane-a05-backend-logs-XXXXXX)
ready_body=$(mktemp /tmp/flowplane-a05-ready-body-XXXXXX)

export FLOWPLANE_HTTP_PORT="${FLOWPLANE_HTTP_PORT:-18085}"
export FLOWPLANE_POSTGRES_PORT="${FLOWPLANE_POSTGRES_PORT:-15435}"
export FLOWPLANE_UID="${FLOWPLANE_UID:-$(id -u)}"
export FLOWPLANE_GID="${FLOWPLANE_GID:-$(id -g)}"
export FLOWPLANE_POSTGRES_USER="${FLOWPLANE_POSTGRES_USER:-flowplane}"
export FLOWPLANE_POSTGRES_PASSWORD="${FLOWPLANE_POSTGRES_PASSWORD:-a05;quote\"verification-only}"
export FLOWPLANE_POSTGRES_DB="${FLOWPLANE_POSTGRES_DB:-flowplane}"

stack_started=false

compose() {
  docker compose --project-name "$project_name" --file "$compose_file" "$@"
}

cleanup() {
  if [ "$stack_started" = true ]; then
    compose down --volumes --remove-orphans >/dev/null 2>&1 || true
  fi

  rm -f "$log_file" "$ready_body"
}

trap cleanup EXIT HUP INT TERM

wait_for_healthy() {
  service=$1
  attempts=90

  while [ "$attempts" -gt 0 ]; do
    container_id=$(compose ps --quiet "$service")
    if [ -n "$container_id" ]; then
      health=$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$container_id")
      if [ "$health" = healthy ]; then
        return 0
      fi
    fi

    attempts=$((attempts - 1))
    sleep 1
  done

  echo "Timed out waiting for $service to become healthy" >&2
  compose ps >&2
  compose logs --no-color "$service" >&2
  return 1
}

wait_for_http_status() {
  path=$1
  expected_status=$2
  attempts=30

  while [ "$attempts" -gt 0 ]; do
    status=$(curl --silent --output /dev/null --write-out '%{http_code}' \
      "http://127.0.0.1:$FLOWPLANE_HTTP_PORT$path" || true)
    if [ "$status" = "$expected_status" ]; then
      return 0
    fi

    attempts=$((attempts - 1))
    sleep 1
  done

  echo "Timed out waiting for $path to return HTTP $expected_status" >&2
  return 1
}

run_migration() {
  FlowPlane__PostgreSql__Host=127.0.0.1 \
  FlowPlane__PostgreSql__Port="$FLOWPLANE_POSTGRES_PORT" \
  FlowPlane__PostgreSql__Database="$FLOWPLANE_POSTGRES_DB" \
  FlowPlane__PostgreSql__Username="$FLOWPLANE_POSTGRES_USER" \
  FlowPlane__PostgreSql__Password="$FLOWPLANE_POSTGRES_PASSWORD" \
    dotnet tool run dotnet-ef database update \
    --project src/backend/FlowPlane.Infrastructure/FlowPlane.Infrastructure.csproj \
    --startup-project src/backend/FlowPlane.Infrastructure/FlowPlane.Infrastructure.csproj \
    --no-build
}

cd "$repository_root"
compose config >/dev/null
stack_started=true
compose up --detach --wait --wait-timeout 120 postgres

compose exec -T postgres pg_isready \
  --username "$FLOWPLANE_POSTGRES_USER" \
  --dbname "$FLOWPLANE_POSTGRES_DB"

dotnet tool restore
dotnet restore src/backend/FlowPlane.Infrastructure/FlowPlane.Infrastructure.csproj \
  --disable-parallel -m:1 --nologo
dotnet build src/backend/FlowPlane.Infrastructure/FlowPlane.Infrastructure.csproj \
  --no-restore --nologo -m:1

run_migration
run_migration

public_tables=$(compose exec -T postgres psql \
  --tuples-only \
  --no-align \
  --username "$FLOWPLANE_POSTGRES_USER" \
  --dbname "$FLOWPLANE_POSTGRES_DB" \
  --command "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name;")
expected_public_tables='DataProtectionKeys
FlowPlaneSecrets
ProviderDiscoveryGenerations
ProviderDiscoveryObservations
ProviderDiscoveryStates
ProviderInstances
__EFMigrationsHistory'
test "$public_tables" = "$expected_public_tables"

migration_count=$(compose exec -T postgres psql \
  --tuples-only \
  --no-align \
  --username "$FLOWPLANE_POSTGRES_USER" \
  --dbname "$FLOWPLANE_POSTGRES_DB" \
  --command 'SELECT count(*) FROM "__EFMigrationsHistory";')
test "$migration_count" = 4

compose up --build --detach --wait --wait-timeout 240 nginx

live_up=$(curl --fail --silent --show-error \
  "http://127.0.0.1:$FLOWPLANE_HTTP_PORT/api/health/live")
ready_up=$(curl --fail --silent --show-error \
  "http://127.0.0.1:$FLOWPLANE_HTTP_PORT/api/health/ready")
test "$live_up" = Healthy
test "$ready_up" = Healthy

compose stop postgres >/dev/null
wait_for_http_status /api/health/ready 503

live_down=$(curl --fail --silent --show-error \
  "http://127.0.0.1:$FLOWPLANE_HTTP_PORT/api/health/live")
ready_down_status=$(curl --silent --output "$ready_body" \
  --write-out '%{http_code}' \
  "http://127.0.0.1:$FLOWPLANE_HTTP_PORT/api/health/ready")
ready_down=$(cat "$ready_body")
test "$live_down" = Healthy
test "$ready_down_status" = 503
test "$ready_down" = Unhealthy

compose start postgres >/dev/null
wait_for_healthy postgres
wait_for_healthy backend
wait_for_healthy nginx
wait_for_http_status /api/health/ready 200

ready_recovered=$(curl --fail --silent --show-error \
  "http://127.0.0.1:$FLOWPLANE_HTTP_PORT/api/health/ready")
test "$ready_recovered" = Healthy

compose restart postgres backend >/dev/null
wait_for_healthy postgres
wait_for_healthy backend
wait_for_healthy nginx

persisted_migration_count=$(compose exec -T postgres psql \
  --tuples-only \
  --no-align \
  --username "$FLOWPLANE_POSTGRES_USER" \
  --dbname "$FLOWPLANE_POSTGRES_DB" \
  --command 'SELECT count(*) FROM "__EFMigrationsHistory";')
test "$persisted_migration_count" = 4

compose logs --no-color backend >"$log_file"
if grep -Fq "$FLOWPLANE_POSTGRES_PASSWORD" "$log_file"; then
  echo "Backend logs contain the PostgreSQL password" >&2
  exit 1
fi
if grep -Eiq '(^|[^[:alpha:]])(password|pwd)=' "$log_file"; then
  echo "Backend logs contain the full PostgreSQL connection string" >&2
  exit 1
fi

echo "POSTGRES_PG_ISREADY=accepting_connections"
echo "POSTGRES_PUBLIC_TABLES=$public_tables"
echo "POSTGRES_MIGRATION_COUNT=$migration_count"
echo "LIVE_WITH_POSTGRES_UP=200_$live_up"
echo "READY_WITH_POSTGRES_UP=200_$ready_up"
echo "LIVE_WITH_POSTGRES_DOWN=200_$live_down"
echo "READY_WITH_POSTGRES_DOWN=${ready_down_status}_$ready_down"
echo "READY_AFTER_POSTGRES_RESTART=200_$ready_recovered"
echo "POSTGRES_MIGRATION_COUNT_AFTER_RESTART=$persisted_migration_count"
echo "BACKEND_LOG_SECRET_SCAN=clean"
echo "A-05 PostgreSQL baseline verification passed"
