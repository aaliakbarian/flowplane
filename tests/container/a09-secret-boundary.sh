#!/bin/sh

set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
compose_file="$repository_root/deploy/compose/compose.yaml"
secret_override="$repository_root/deploy/compose/compose.secrets.yaml"
project_name="flowplane-a09-secret-verification-$$"
secret_directory=$(mktemp -d /tmp/flowplane-a09-secret-verification-XXXXXX)
secret_file="$secret_directory/flowplane-root.pfx"

export FLOWPLANE_UID="${FLOWPLANE_UID:-$(id -u)}"
export FLOWPLANE_GID="${FLOWPLANE_GID:-$(id -g)}"
export FLOWPLANE_POSTGRES_PORT="${FLOWPLANE_POSTGRES_PORT:-15449}"
export FLOWPLANE_POSTGRES_USER="${FLOWPLANE_POSTGRES_USER:-flowplane}"
export FLOWPLANE_POSTGRES_PASSWORD="${FLOWPLANE_POSTGRES_PASSWORD:-a09-postgres-verification-only}"
export FLOWPLANE_POSTGRES_DB="${FLOWPLANE_POSTGRES_DB:-flowplane}"
export FLOWPLANE_SECRET_ROOT_CERTIFICATE_FILE="$secret_file"

stack_started=false

compose() {
  docker compose \
    --project-name "$project_name" \
    --file "$compose_file" \
    --file "$secret_override" \
    "$@"
}

cleanup() {
  if [ "$stack_started" = true ]; then
    compose down --volumes --remove-orphans >/dev/null 2>&1 || true
  fi

  rm -rf "$secret_directory"
}

trap cleanup EXIT HUP INT TERM

cd "$repository_root"
sh deploy/compose/create-development-root-certificate.sh "$secret_file"
chmod 0600 "$secret_file"
certificate_checksum=$(sha256sum "$secret_file")
if sh deploy/compose/create-development-root-certificate.sh "$secret_file" >/dev/null 2>&1; then
  echo "Development root-certificate generator overwrote an existing root" >&2
  exit 1
fi
test "$(sha256sum "$secret_file")" = "$certificate_checksum"
chmod 0400 "$secret_file"
test "$(stat -c '%u' "$secret_file")" = "$FLOWPLANE_UID"
test "$(stat -c '%g' "$secret_file")" = "$FLOWPLANE_GID"
test "$(stat -c '%a' "$secret_file")" = 400

docker compose --file "$compose_file" config >/dev/null
compose config >/dev/null

stack_started=true
compose up --detach --wait --wait-timeout 120 postgres
compose build backend

mount_result=$(compose run --rm --no-deps --entrypoint bash backend -ec '
  test "$(id -u)" = "'"$FLOWPLANE_UID"'"
  test "$(id -g)" = "'"$FLOWPLANE_GID"'"
  test "$(stat -c %a /run/secrets/flowplane_root_certificate)" = 400
  test -r /run/secrets/flowplane_root_certificate
  test ! -w /run/secrets/flowplane_root_certificate
  test -z "$(find /app -type f -name "'"'*.pfx'"'" -print -quit)"
  printf readable-readonly
')
test "$mount_result" = readable-readonly

FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING="Host=127.0.0.1;Port=$FLOWPLANE_POSTGRES_PORT;Database=$FLOWPLANE_POSTGRES_DB;Username=$FLOWPLANE_POSTGRES_USER;Password=$FLOWPLANE_POSTGRES_PASSWORD" \
  dotnet test tests/integration/FlowPlane.Persistence.Tests/FlowPlane.Persistence.Tests.csproj \
  --no-restore --nologo -m:1

compose up --detach --wait --wait-timeout 120 backend
backend_id=$(compose ps --quiet backend)
backend_secret_mounts=$(docker inspect --format \
  '{{range .Mounts}}{{if eq .Destination "/run/secrets/flowplane_root_certificate"}}{{printf "%s:%t" .Destination .RW}}{{end}}{{end}}' \
  "$backend_id")
test "$backend_secret_mounts" = /run/secrets/flowplane_root_certificate:false

docker exec "$backend_id" bash -ec '
  test -r /run/secrets/flowplane_root_certificate
  exec 3<>/dev/tcp/127.0.0.1/8080
  printf "GET /health/ready HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3
  grep -q " 200 " <&3
'

echo "ROOT_CERTIFICATE_HOST_MODE=400"
echo "BACKEND_RUNTIME_UID_GID=$FLOWPLANE_UID:$FLOWPLANE_GID"
echo "BACKEND_SECRET_MOUNT=readable_readonly"
echo "MANAGED_SECRET_POSTGRES_TESTS=passed_without_skips"
echo "SECRET_ENABLED_BACKEND_READY=healthy"
echo "A-09 secret-boundary verification passed"
