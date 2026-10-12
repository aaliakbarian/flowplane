#!/bin/sh

set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
compose_file="$repository_root/deploy/compose/compose.yaml"
project_name="flowplane-a07-verification-$$"
verification_dir=$(mktemp -d /tmp/flowplane-a07-verification-XXXXXX)
root_body="$verification_dir/root.html"
root_headers="$verification_dir/root.headers"
asset_body="$verification_dir/asset.js"
asset_headers="$verification_dir/asset.headers"
route_body="$verification_dir/route.html"
callback_body="$verification_dir/callback.html"
api_body="$verification_dir/api.body"
token_evidence_file="$verification_dir/tokens"
playwright_log="$verification_dir/playwright.log"
compose_log="$verification_dir/compose.log"
combined_log="$verification_dir/combined.log"
static_dir="$verification_dir/static"

export FLOWPLANE_UID="$(id -u)"
export FLOWPLANE_GID="$(id -g)"
export FLOWPLANE_HTTP_PORT=8080
export FLOWPLANE_KEYCLOAK_PORT=8081
export FLOWPLANE_POSTGRES_PORT=5432
export FLOWPLANE_POSTGRES_PASSWORD=a07-postgres-verification-only
export FLOWPLANE_KEYCLOAK_ADMIN_PASSWORD=a07-admin-verification-only
export FLOWPLANE_KEYCLOAK_TEST_USERNAME=flowplane-developer
export FLOWPLANE_KEYCLOAK_TEST_PASSWORD=a07-browser-verification-only
export COMPOSE_PROGRESS=plain

compose() {
  docker compose \
    --project-name "$project_name" \
    --file "$compose_file" \
    --profile production \
    "$@"
}

cleanup() {
  compose down --volumes --remove-orphans >/dev/null 2>&1 || true
  rm -rf -- "$verification_dir"
}
trap cleanup EXIT HUP INT TERM

compose down --volumes --remove-orphans >/dev/null 2>&1 || true
compose config --quiet
compose up --build --detach --wait --wait-timeout 240 frontend-production

running_services=$(compose ps --services --status running)
for service in frontend-production backend postgres keycloak
do
  printf '%s\n' "$running_services" | grep -Fx "$service" >/dev/null
done
if printf '%s\n' "$running_services" | grep -Eq '^(nginx|frontend-dev)$'
then
  echo "The production path started a development frontend service" >&2
  exit 1
fi

frontend_container=$(compose ps --quiet frontend-production)
test -n "$frontend_container"
frontend_image=$(docker inspect --format '{{.Config.Image}}' "$frontend_container")
frontend_ports=$(docker inspect --format '{{json .NetworkSettings.Ports}}' "$frontend_container")
case "$frontend_ports" in
  *'"80/tcp"'*'"HostPort":"8080"'*) ;;
  *)
    echo "The production frontend did not publish only the expected browser port: $frontend_ports" >&2
    exit 1
    ;;
esac
case "$frontend_ports" in
  *'5173'*)
    echo "The production frontend unexpectedly exposes the Vite port" >&2
    exit 1
    ;;
esac

compose exec -T frontend-production nginx -t
runtime_process=$(compose exec -T frontend-production sh -ec 'cat /proc/1/comm')
test "$runtime_process" = nginx
compose exec -T frontend-production sh -ec '! command -v node && ! command -v vite'
compose exec -T frontend-production sh -ec 'test ! -e /workspace && test ! -e /usr/share/nginx/html/node_modules'
image_source_artifacts=$(compose exec -T frontend-production sh -ec \
  'find / -xdev \( -type d -name node_modules -o -type f \( -name "*.ts" -o -name "*.tsx" -o -name "*.map" -o -name "package.json" -o -name ".env" -o -name ".env.*" \) \) -print' \
  2>/dev/null)
test -z "$image_source_artifacts"

curl --fail --silent --show-error \
  --dump-header "$root_headers" \
  --output "$root_body" \
  http://127.0.0.1:8080/
grep -Fq '<div id="root"></div>' "$root_body"
grep -Eiq '^Cache-Control: no-cache' "$root_headers"

asset_path=$(sed -n 's/.*src="\([^"?]*\/assets\/[^"?]*\.js\)".*/\1/p' "$root_body")
test -n "$asset_path"
curl --fail --silent --show-error \
  --dump-header "$asset_headers" \
  --output "$asset_body" \
  "http://127.0.0.1:8080$asset_path"
test -s "$asset_body"
grep -Eiq '^Cache-Control: public, max-age=31536000, immutable' "$asset_headers"

curl --fail --silent --show-error \
  --output "$route_body" \
  http://127.0.0.1:8080/some/non-api/route
cmp "$root_body" "$route_body"

curl --fail --silent --show-error \
  --output "$callback_body" \
  http://127.0.0.1:8080/oidc/callback
cmp "$root_body" "$callback_body"

live_response=$(curl --fail --silent --show-error http://127.0.0.1:8080/api/health/live)
ready_response=$(curl --fail --silent --show-error http://127.0.0.1:8080/api/health/ready)
test "$live_response" = Healthy
test "$ready_response" = Healthy

me_status=$(curl --silent --show-error \
  --output "$api_body" \
  --write-out '%{http_code}' \
  http://127.0.0.1:8080/api/me)
test "$me_status" = 401
! grep -Fq '<div id="root"></div>' "$api_body"

missing_api_status=$(curl --silent --show-error \
  --output "$api_body" \
  --write-out '%{http_code}' \
  http://127.0.0.1:8080/api/not-a-flowplane-route)
test "$missing_api_status" = 404
! grep -Fq '<div id="root"></div>' "$api_body"

mkdir "$static_dir"
docker cp "$frontend_container:/usr/share/nginx/html/." "$static_dir"
test -f "$static_dir/index.html"
test -f "$static_dir${asset_path}"
test -z "$(find "$static_dir" -type d -name node_modules -print -quit)"
test -z "$(find "$static_dir" -type f \( -name '*.ts' -o -name '*.tsx' -o -name '*.map' -o -name '.env' -o -name '.env.*' \) -print -quit)"

image_config=$(docker image inspect "$frontend_image" \
  --format 'cmd={{json .Config.Cmd}} ports={{json .Config.ExposedPorts}} env={{json .Config.Env}}')
printf '%s\n' "$image_config" | grep -Fq 'daemon off;'
printf '%s\n' "$image_config" | grep -Fq '"80/tcp"'
! printf '%s\n' "$image_config" | grep -Fq '5173'

if ! docker run --rm \
  --network host \
  --mount "type=bind,source=$repository_root/src/frontend,target=/source,readonly" \
  --mount "type=bind,source=$verification_dir,target=/evidence" \
  --workdir /tmp \
  --env FLOWPLANE_BASE_URL=http://127.0.0.1:8080 \
  --env FLOWPLANE_TEST_USERNAME="$FLOWPLANE_KEYCLOAK_TEST_USERNAME" \
  --env FLOWPLANE_TEST_PASSWORD="$FLOWPLANE_KEYCLOAK_TEST_PASSWORD" \
  --env FLOWPLANE_TOKEN_EVIDENCE_FILE=/evidence/tokens \
  --env FLOWPLANE_EVIDENCE_UID="$FLOWPLANE_UID" \
  --env FLOWPLANE_EVIDENCE_GID="$FLOWPLANE_GID" \
  mcr.microsoft.com/playwright:v1.64.0-noble \
  bash -lc 'cp -a /source /tmp/frontend && cd /tmp/frontend && npm ci --ignore-scripts && npm run test:e2e' \
  >"$playwright_log" 2>&1
then
  sed -n '1,260p' "$playwright_log"
  exit 1
fi
sed -n '1,260p' "$playwright_log"

compose logs --no-color >"$compose_log" 2>&1
cat "$compose_log" "$playwright_log" >"$combined_log"

for forbidden_value in \
  "$FLOWPLANE_KEYCLOAK_ADMIN_PASSWORD" \
  "$FLOWPLANE_KEYCLOAK_TEST_PASSWORD" \
  "$FLOWPLANE_POSTGRES_PASSWORD" \
  keycloak-local-development-only \
  flowplane-developer-local-only \
  flowplane-local-development-only
do
  if grep -FRq -- "$forbidden_value" "$static_dir"
  then
    echo "A development credential leaked into the static frontend: $forbidden_value" >&2
    exit 1
  fi
done

while IFS= read -r token || test -n "$token"
do
  if test -n "$token" && grep -FRq -- "$token" "$static_dir"
  then
    echo "A development token leaked into the static frontend" >&2
    exit 1
  fi
  if test -n "$token" && grep -Fq -- "$token" "$combined_log"
  then
    echo "A development token leaked into verification output" >&2
    exit 1
  fi
done <"$token_evidence_file"

if grep -Fq -- "$FLOWPLANE_KEYCLOAK_TEST_PASSWORD" "$combined_log"
then
  echo "The development browser password leaked into verification output" >&2
  exit 1
fi
if grep -Eiq -- 'authorization:[[:space:]]*bearer[[:space:]]+[A-Za-z0-9._-]+' "$combined_log"
then
  echo "A bearer credential leaked into verification output" >&2
  exit 1
fi

client_secret_occurrences=$(grep -FRoh -- 'client_secret' "$static_dir" | wc -l)
hard_coded_client_secret=$(grep -Eirho -- '["]?client_secret["]?[[:space:]]*[:=][[:space:]]*"[^"]+"' "$static_dir" || true)
test -z "$hard_coded_client_secret"

printf '%s\n' \
  "PRODUCTION_RUNNING_SERVICES=$(printf '%s' "$running_services" | tr '\n' ',')" \
  "PRODUCTION_FRONTEND_PORTS=$frontend_ports" \
  "PRODUCTION_RUNTIME_PROCESS=$runtime_process" \
  "PRODUCTION_IMAGE_CONFIG=$image_config" \
  "PRODUCTION_IMAGE_SOURCE_ENV_NODE_MODULES_SCAN=clean" \
  "ROOT_CACHE_CONTROL=no-cache" \
  "STATIC_ASSET=$asset_path" \
  "STATIC_ASSET_CACHE_CONTROL=public,max-age=31536000,immutable" \
  "DIRECT_SPA_ROUTE=index.html" \
  "OIDC_CALLBACK_ROUTE=index.html" \
  "API_LIVE=200_$live_response" \
  "API_READY=200_$ready_response" \
  "API_ME_ANONYMOUS=${me_status}_not_index_html" \
  "API_MISS=${missing_api_status}_not_index_html" \
  "CLIENT_SECRET_LIBRARY_IDENTIFIER_OCCURRENCES=$client_secret_occurrences" \
  "STATIC_CREDENTIAL_AND_TOKEN_SCAN=clean" \
  "OIDC_AUTHORIZATION_CODE_PKCE_PRODUCTION_PATH=ok" \
  "A-07 production frontend verification passed"
