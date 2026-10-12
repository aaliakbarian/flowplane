#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
compose_file="$repository_root/deploy/compose/compose.yaml"
project_name=flowplane-a06-verification
verification_dir=$(mktemp -d /tmp/flowplane-a06-verification-XXXXXX)
token_evidence_file="$verification_dir/tokens"
playwright_log="$verification_dir/playwright.log"
compose_log="$verification_dir/compose.log"
combined_log="$verification_dir/combined.log"

export FLOWPLANE_UID="$(id -u)"
export FLOWPLANE_GID="$(id -g)"
export FLOWPLANE_HTTP_PORT=8080
export FLOWPLANE_KEYCLOAK_PORT=8081
export FLOWPLANE_POSTGRES_PORT=5432
export FLOWPLANE_POSTGRES_PASSWORD=a06-postgres-verification-only
export FLOWPLANE_KEYCLOAK_ADMIN_PASSWORD=a06-admin-verification-only
export FLOWPLANE_KEYCLOAK_TEST_USERNAME=flowplane-developer
export FLOWPLANE_KEYCLOAK_TEST_PASSWORD=a06-browser-verification-only
export COMPOSE_PROGRESS=plain

compose() {
  docker compose --project-name "$project_name" --file "$compose_file" "$@"
}

cleanup() {
  compose down --volumes --remove-orphans >/dev/null 2>&1 || true
  rm -rf -- "$verification_dir"
}
trap cleanup EXIT HUP INT TERM

compose down --volumes --remove-orphans >/dev/null 2>&1 || true
compose config --quiet
compose up --build --wait

echo "internal-discovery-verification=starting"
internal_discovery=$(
  compose exec -T frontend-dev node --input-type=module --eval '
    const metadataAddress = "http://keycloak:8080/realms/flowplane/.well-known/openid-configuration";
    const expectedIssuer = "http://127.0.0.1:8081/realms/flowplane";
    const metadataResponse = await fetch(metadataAddress);
    if (!metadataResponse.ok) throw new Error(`metadata status ${metadataResponse.status}`);
    const metadata = await metadataResponse.json();
    if (metadata.issuer !== expectedIssuer) throw new Error(`unexpected issuer ${metadata.issuer}`);
    if (!metadata.jwks_uri.startsWith("http://keycloak:8080/")) {
      throw new Error(`JWKS is not using the dynamic backchannel: ${metadata.jwks_uri}`);
    }
    const jwksResponse = await fetch(metadata.jwks_uri);
    if (!jwksResponse.ok) throw new Error(`JWKS status ${jwksResponse.status}`);
    const jwks = await jwksResponse.json();
    if (!Array.isArray(jwks.keys) || jwks.keys.length === 0) throw new Error("JWKS has no keys");
    console.log(`issuer=${metadata.issuer} jwks=${metadata.jwks_uri} keys=${jwks.keys.length}`);
  '
)
printf '%s\n' "$internal_discovery"

echo "host-port-isolation-verification=starting"
frontend_ports=$(
  docker inspect "$(compose ps --quiet frontend-dev)" \
    --format '{{json .NetworkSettings.Ports}}'
)
backend_ports=$(
  docker inspect "$(compose ps --quiet backend)" \
    --format '{{json .NetworkSettings.Ports}}'
)
case "$frontend_ports$backend_ports" in
  *HostPort*)
    echo "The frontend or backend unexpectedly publishes a host port" >&2
    exit 1
    ;;
esac
printf 'frontend-ports=%s backend-ports=%s\n' "$frontend_ports" "$backend_ports"

echo "health-regression-verification=starting"
curl --fail --silent http://127.0.0.1:8080/api/health/live
curl --fail --silent http://127.0.0.1:8080/api/health/ready

echo "playwright-verification=starting"
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

if grep -Fq -- "$FLOWPLANE_KEYCLOAK_TEST_PASSWORD" "$combined_log"
then
  echo "A development test password leaked into verification output" >&2
  exit 1
fi

while IFS= read -r token
do
  if test -n "$token" && grep -Fq -- "$token" "$combined_log"
  then
    echo "A development token leaked into verification output" >&2
    exit 1
  fi
done <"$token_evidence_file"

if grep -Eiq -- 'client_secret|authorization:[[:space:]]*bearer[[:space:]]+[A-Za-z0-9._-]+' "$combined_log"
then
  echo "A client secret or bearer credential leaked into verification output" >&2
  exit 1
fi

printf '%s\n' \
  "fresh-realm-import=ok" \
  "authorization-code-pkce-s256=ok" \
  "flowplane-api-audience=ok" \
  "same-origin-protected-api=ok" \
  "password-and-token-redaction=ok"
