#!/bin/sh

set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
compose_file="$repository_root/deploy/compose/compose.yaml"
project_name="flowplane-a04-verification-$$"
frontend_source="$repository_root/src/frontend"

export FLOWPLANE_HTTP_PORT="${FLOWPLANE_HTTP_PORT:-18080}"
export FLOWPLANE_POSTGRES_PORT="${FLOWPLANE_POSTGRES_PORT:-15432}"
export FLOWPLANE_KEYCLOAK_PORT="${FLOWPLANE_KEYCLOAK_PORT:-18081}"
export FLOWPLANE_UID="${FLOWPLANE_UID:-$(id -u)}"
export FLOWPLANE_GID="${FLOWPLANE_GID:-$(id -g)}"
export FLOWPLANE_POSTGRES_USER="${FLOWPLANE_POSTGRES_USER:-flowplane}"
export FLOWPLANE_POSTGRES_PASSWORD="${FLOWPLANE_POSTGRES_PASSWORD:-a04-verification-only}"
export FLOWPLANE_POSTGRES_DB="${FLOWPLANE_POSTGRES_DB:-flowplane}"
export FLOWPLANE_KEYCLOAK_ADMIN="${FLOWPLANE_KEYCLOAK_ADMIN:-admin}"
export FLOWPLANE_KEYCLOAK_ADMIN_PASSWORD="${FLOWPLANE_KEYCLOAK_ADMIN_PASSWORD:-a04-verification-only}"

stack_started=false
hmr_client_pid=
hmr_log=
source_backup=

compose() {
  docker compose --project-name "$project_name" --file "$compose_file" "$@"
}

cleanup() {
  if [ -n "$hmr_client_pid" ]; then
    kill "$hmr_client_pid" 2>/dev/null || true
    wait "$hmr_client_pid" 2>/dev/null || true
  fi

  if [ -n "$source_backup" ] && [ -f "$source_backup" ]; then
    cp "$source_backup" "$frontend_source/src/App.tsx"
  fi

  if [ "$stack_started" = true ]; then
    compose down --volumes --remove-orphans >/dev/null 2>&1 || true
  fi

  if [ -n "$hmr_log" ]; then
    rm -f "$hmr_log"
  fi

  if [ -n "$source_backup" ]; then
    rm -f "$source_backup"
  fi
}

trap cleanup EXIT HUP INT TERM

wait_for_log() {
  expected=$1
  file=$2
  attempts=30

  while [ "$attempts" -gt 0 ]; do
    if grep -Fq "$expected" "$file"; then
      return 0
    fi

    attempts=$((attempts - 1))
    sleep 1
  done

  echo "Timed out waiting for '$expected' in $file" >&2
  sed -n '1,200p' "$file" >&2
  return 1
}

wait_for_healthy() {
  service=$1
  attempts=60

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

test -f "$compose_file"

compose config >/dev/null
stack_started=true
compose up --build --detach --wait --wait-timeout 240

running_services=$(compose ps --services --status running)
for service in nginx frontend-dev backend postgres keycloak; do
  echo "$running_services" | grep -Fx "$service" >/dev/null
done

curl --fail --silent --show-error "http://127.0.0.1:$FLOWPLANE_HTTP_PORT/" >/dev/null

live_response=$(curl --fail --silent --show-error "http://127.0.0.1:$FLOWPLANE_HTTP_PORT/api/health/live")
ready_response=$(curl --fail --silent --show-error "http://127.0.0.1:$FLOWPLANE_HTTP_PORT/api/health/ready")
test "$live_response" = Healthy
test "$ready_response" = Healthy

frontend_container=$(compose ps --quiet frontend-dev)
frontend_port_bindings=$(docker inspect --format '{{json .HostConfig.PortBindings}}' "$frontend_container")
if [ "$frontend_port_bindings" != '{}' ]; then
  echo "frontend-dev must not publish a host port" >&2
  exit 1
fi

backend_container=$(compose ps --quiet backend)
backend_port_bindings=$(docker inspect --format '{{json .HostConfig.PortBindings}}' "$backend_container")
if [ "$backend_port_bindings" != '{}' ]; then
  echo "backend must not publish a host port" >&2
  exit 1
fi

hmr_log=$(mktemp /tmp/flowplane-a04-hmr-XXXXXX)
source_backup=$(mktemp /tmp/flowplane-a04-app-XXXXXX)
cp "$frontend_source/src/App.tsx" "$source_backup"
frontend_image=$(docker inspect --format '{{.Config.Image}}' "$frontend_container")
source_probe="A04_REBUILD_PROBE_$$"

docker run --rm --network host \
  --env "FLOWPLANE_HTTP_PORT=$FLOWPLANE_HTTP_PORT" \
  "$frontend_image" \
  node --input-type=module --eval '
const socket = new WebSocket(`ws://127.0.0.1:${process.env.FLOWPLANE_HTTP_PORT}/`, "vite-hmr")
const timeout = setTimeout(() => {
  console.error("HMR_TIMEOUT")
  process.exit(1)
}, 30000)

socket.addEventListener("open", () => console.log("HMR_WEBSOCKET_OPEN"))
socket.addEventListener("message", (event) => {
  const message = String(event.data)
  console.log(message)
  if (message.includes("\"type\":\"update\"")) {
    clearTimeout(timeout)
    console.log("HMR_UPDATE_RECEIVED")
    socket.close()
    process.exit(0)
  }
})
socket.addEventListener("error", (event) => {
  console.error("HMR_WEBSOCKET_ERROR", event)
  process.exit(1)
})
' >"$hmr_log" 2>&1 &
hmr_client_pid=$!

wait_for_log HMR_WEBSOCKET_OPEN "$hmr_log"
printf '\nexport const a04ContainerRebuildProbe = "%s"\n' "$source_probe" >> "$frontend_source/src/App.tsx"
wait_for_log HMR_UPDATE_RECEIVED "$hmr_log"
wait "$hmr_client_pid"
hmr_client_pid=
grep -E 'HMR_WEBSOCKET_OPEN|HMR_UPDATE_RECEIVED' "$hmr_log"

nginx_container=$(compose ps --quiet nginx)
compose rm --stop --force nginx frontend-dev backend >/dev/null
compose up --build --detach --wait --wait-timeout 120 nginx frontend-dev backend

test "$frontend_container" != "$(compose ps --quiet frontend-dev)"
test "$backend_container" != "$(compose ps --quiet backend)"
test "$nginx_container" != "$(compose ps --quiet nginx)"
grep -Fq "$source_probe" "$frontend_source/src/App.tsx"
curl --fail --silent --show-error "http://127.0.0.1:$FLOWPLANE_HTTP_PORT/src/App.tsx" \
  | grep -Fq "$source_probe"
echo "APPLICATION_CONTAINER_REBUILD_SOURCE_PROBE=$source_probe"

cp "$source_backup" "$frontend_source/src/App.tsx"
rm -f "$source_backup"
source_backup=

compose exec -T postgres pg_isready \
  --username "$FLOWPLANE_POSTGRES_USER" \
  --dbname "$FLOWPLANE_POSTGRES_DB" >/dev/null

compose exec -T postgres sh -ec 'test "$PGDATA" = /var/lib/postgresql/18/docker'

postgres_container=$(compose ps --quiet postgres)
postgres_volume=$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql"}}{{.Name}}{{end}}{{end}}' "$postgres_container")
test -n "$postgres_volume"
docker volume inspect "$postgres_volume" >/dev/null

compose exec -T postgres psql \
  --username "$FLOWPLANE_POSTGRES_USER" \
  --dbname "$FLOWPLANE_POSTGRES_DB" \
  --set ON_ERROR_STOP=1 \
  --command 'CREATE TABLE a04_persistence_probe (marker text PRIMARY KEY);' >/dev/null

compose exec -T postgres psql \
  --username "$FLOWPLANE_POSTGRES_USER" \
  --dbname "$FLOWPLANE_POSTGRES_DB" \
  --set ON_ERROR_STOP=1 \
  --command "INSERT INTO a04_persistence_probe(marker) VALUES ('persists');" >/dev/null

compose restart postgres >/dev/null
wait_for_healthy postgres

persisted_marker=$(compose exec -T postgres psql \
  --tuples-only \
  --no-align \
  --username "$FLOWPLANE_POSTGRES_USER" \
  --dbname "$FLOWPLANE_POSTGRES_DB" \
  --command 'SELECT marker FROM a04_persistence_probe;')
test "$persisted_marker" = persists
echo "POSTGRES_PERSISTED_MARKER=$persisted_marker"

compose exec -T postgres psql \
  --username "$FLOWPLANE_POSTGRES_USER" \
  --dbname "$FLOWPLANE_POSTGRES_DB" \
  --set ON_ERROR_STOP=1 \
  --command 'DROP TABLE a04_persistence_probe;' >/dev/null

keycloak_status=$(curl --silent --output /dev/null --write-out '%{http_code}' "http://127.0.0.1:$FLOWPLANE_KEYCLOAK_PORT/")
case "$keycloak_status" in
  2??|3??) ;;
  *)
    echo "Unexpected Keycloak HTTP status: $keycloak_status" >&2
    exit 1
    ;;
esac
echo "KEYCLOAK_HTTP_STATUS=$keycloak_status"

compose stop postgres keycloak >/dev/null
test "$(curl --fail --silent --show-error "http://127.0.0.1:$FLOWPLANE_HTTP_PORT/api/health/live")" = Healthy
test "$(curl --fail --silent --show-error "http://127.0.0.1:$FLOWPLANE_HTTP_PORT/api/health/ready")" = Healthy
compose start postgres keycloak >/dev/null
wait_for_healthy postgres
wait_for_healthy keycloak

test ! -e "$frontend_source/node_modules"

wrong_owner=$(find "$frontend_source" -xdev ! -user "$(id -u)" -print -quit)
test -z "$wrong_owner"
wrong_group=$(find "$frontend_source" -xdev ! -group "$(id -g)" -print -quit)
test -z "$wrong_group"
verified_ownership=$(stat --format '%u:%g' "$frontend_source/src/App.tsx")
test "$verified_ownership" = "$(id -u):$(id -g)"

echo "FRONTEND_AND_BACKEND_HOST_PORTS=unpublished"
echo "BACKEND_HEALTH_WITH_POSTGRES_AND_KEYCLOAK_STOPPED=Healthy"
echo "FRONTEND_SOURCE_OWNERSHIP=$verified_ownership"
echo "A-04 development stack verification passed"
