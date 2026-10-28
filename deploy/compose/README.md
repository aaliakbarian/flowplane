# FlowPlane development Compose stack

`compose.yaml` is the primary development Compose file. It provides two alternate frontend serving modes over the same backend, PostgreSQL, and Keycloak foundation: the default Vite/HMR development path and the opt-in production-style static Nginx path. The optional `compose.secrets.yaml` override adds only the A-09 external root-certificate mount for built-in provider-secret operations.

Copy `.env.example` to the ignored `.env` file, then set `FLOWPLANE_UID` and `FLOWPLANE_GID` to the values reported by `id -u` and `id -g` in WSL. The committed example contains local-development-only bootstrap values, not production or shared credentials. Database passwords, Keycloak bootstrap credentials, browser test credentials, and tokens are secret values even in development; override them only in the ignored `.env` and never commit or log them. Browser-facing OIDC authority and public client ID values are intentionally public configuration and must not be mistaken for secrets.

From the repository root, validate and start the stack with:

```sh
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml config
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml up --build --wait
```

The normal browser entry point is `http://127.0.0.1:${FLOWPLANE_HTTP_PORT}` (port `8080` by default). Nginx strips the `/api` prefix when forwarding API requests, so `/api/health/live` and `/api/health/ready` reach the backend's existing `/health/live` and `/health/ready` endpoints. Frontend HTTP and Vite HMR/WebSocket traffic also traverse Nginx; neither Vite nor the backend publishes a host port.

PostgreSQL is available to WSL at `127.0.0.1:${FLOWPLANE_POSTGRES_PORT}` and stores PostgreSQL 18 data in the named volume mounted at `/var/lib/postgresql`. Keycloak development mode is available at `http://127.0.0.1:${FLOWPLANE_KEYCLOAK_PORT}`.

## Production-style frontend path

Both frontend modes intentionally use the same FlowPlane browser port. When switching an already healthy development stack, stop only development Nginx and start the static frontend without recreating its backend, PostgreSQL or Keycloak dependencies:

```sh
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml stop nginx
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml --profile production up --build --no-deps --wait frontend-production
```

Use the same environment file and all existing private/secrets overrides on every command. `--no-deps` requires backend, PostgreSQL and Keycloak to be running and healthy already; for a fresh startup, bring up those approved dependencies first. Do not recreate Keycloak just to switch frontend modes: its development data has no persistent volume, so recreation can invalidate the operator subject bootstrap. Targeting only `frontend-production` avoids starting development Nginx on the same port. The browser origin remains `http://127.0.0.1:${FLOWPLANE_HTTP_PORT}` (`8080` by default), while Keycloak remains at port `8081` by default.

Restore the original development Nginx after verification:

```sh
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml stop frontend-production
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml start --wait nginx
```

The production target builds React/TypeScript with Vite, copies `dist/` into `nginx:1.30.5-alpine3.24`, serves hashed assets directly, falls back to `index.html` for non-API SPA paths such as `/oidc/callback`, and preserves the same `/api/` prefix-stripping proxy behavior as development. It does not contain or run Node.js or Vite. The OIDC authority and public client ID are build-time browser configuration, not secrets; a different public authority requires rebuilding this static image.

Run the bounded production-path verification with:

```sh
sh tests/container/a07-production-frontend.sh
```

As of A-05, backend readiness includes PostgreSQL connectivity while liveness remains process-only. Compose supplies separate `FlowPlane__PostgreSql__*` configuration values so Npgsql can safely construct the connection string even when credentials contain connection-string delimiters. Credentials are not stored in source configuration or written to logs.

The backend binds complete-scan SSIS settings from the provider-owned `FlowPlane:Ssis` section. Compose exposes `FLOWPLANE_SSIS_MAX_PROJECTS_PER_BATCH` (20), `FLOWPLANE_SSIS_CONNECTION_TIMEOUT_SECONDS` (5), `FLOWPLANE_SSIS_COMMAND_TIMEOUT_SECONDS` (15), and `FLOWPLANE_SSIS_OVERALL_TIMEOUT_SECONDS` (1800). These are operator settings, not ProviderInstance metadata. Project batching and bounded streaming buffers do not cap total catalog size or truncate project metadata. The old MaxCandidates/MaxParametersPerCandidate/MaxEnvironmentReferencesPerProject configuration keys are rejected; remove their retired overrides when updating local configuration. No SSIS server is required for startup. See [SSIS Provider Integration Testing](../../docs/development/ssis-integration-testing.md#a-11-discovery-configuration) for ranges, provisional observation delivery and consistency/failure semantics.

## Optional built-in provider-secret protection

The default stack still boots without secret-protection root material. In that mode PostgreSQL, OIDC, health, and provider-independent behavior remain available, while `flowplane:` secret create, replace, delete, and resolve operations return a safe protection-unavailable error. Independent `env:` references remain a development/testing convenience and are never persisted by FlowPlane.

Secret resolution uses a bounded callback so application code does not gain a general-purpose plaintext read API and resolved values have a short practical lifetime. This does not promise perfect memory zeroization: process-environment strings and immutable strings created by downstream .NET APIs cannot necessarily be erased on demand.

For the A-09 reference path, create an unencrypted development-only PKCS#12/PFX file outside Git, then add the optional Compose override:

```sh
sh deploy/compose/create-development-root-certificate.sh
docker compose \
  --env-file deploy/compose/.env \
  -f deploy/compose/compose.yaml \
  -f deploy/compose/compose.secrets.yaml \
  up --build --wait
```

The generator uses a restrictive `umask` and sets the PFX to mode `0400`. `compose.secrets.yaml` runs the backend with the configured WSL `FLOWPLANE_UID`/`FLOWPLANE_GID`, mounts the PFX read-only at `/run/secrets/flowplane_root_certificate`, and supplies it only to `backend`; Nginx, frontend, PostgreSQL, and Keycloak do not receive it. The unencrypted PFX is a development/reference bootstrap choice delivered through a Docker Compose secret, not enterprise key management. Do not make it world-readable, copy it into an image, commit it, or pass it as an ordinary environment variable.

FlowPlane persists authenticated provider-secret ciphertext and the Data Protection key ring in PostgreSQL. The key ring is explicitly protected by the external certificate. Back up PostgreSQL data and the required certificate/private key separately: a database backup alone cannot decrypt provider credentials, while loss of the certificate can make them unrecoverable. Retain old Data Protection keys and overlapping root certificates for existing ciphertext during future rotation. ASP.NET Core Data Protection is intentionally used for application-owned, replaceable provider credentials; it is not claimed as a general indefinite archival secret system.

For explicitly disposable local acceptance data, loss and re-entry of test credentials is accepted: independent off-host PFX backup, a backup before every test and an extra acceptance database are not prerequisites. Do not delete/regenerate existing root material or relax encryption/permissions during testing. Recoverable non-disposable and production deployments retain the backup/recovery requirements above. See the [development policy and live evidence](../../docs/development/milestones/milestone-a-live-acceptance.md#disposable-development-data-and-deployment-readiness).

## Provider runtime preparation

Authentication alone does not authorize provider administration. Configure exact `FlowPlane:Operators:Allowed` pairs using the configured OIDC issuer and subject obtained from authenticated `/api/me`, and exact `FlowPlane:Ssis:AllowedEndpoints` host/port pairs, through a private backend-only override. Empty policies deny access; never enable a wildcard endpoint or accept browser-selected secret locators. See the [Stage 1 contract](../../docs/development/a12-stage1-api.md).

After explicit application of all committed migrations, `FlowPlane__DiscoveryHosting__Enabled=true` enables the existing discovery-specific runner. It defaults to false. Preserve the private operator/endpoint configuration and secret override when recreating only the backend to apply settings. See [hosting configuration](../../docs/development/a12-stage2-discovery.md#configuration-and-gate-b-operation), [inventory disclosure](../../docs/development/a12-stage3-inventory.md) and the [UI journey](../../docs/development/a12-stage4-ui.md). Startup does not create providers or admit new refreshes; the runner may recover already accepted work.

## Apply committed PostgreSQL migrations

Migrations are explicit and are not applied automatically when the application starts. From the repository root, use the wrapper to resolve the ignored Compose environment file as data, restore the pinned local EF tool, and apply migrations to the WSL-accessible PostgreSQL port:

```sh
sh deploy/compose/apply-migrations.sh
```

The wrapper does not source `.env` as shell code and does not print the resolved password. Pass a different environment-file path as its only argument when needed. The current committed schema has four migrations and seven public tables: `__EFMigrationsHistory`, `ProviderInstances`, `FlowPlaneSecrets`, `DataProtectionKeys`, `ProviderDiscoveryStates`, `ProviderDiscoveryGenerations` and `ProviderDiscoveryObservations`. A-09 introduced provider/secret/key-ring storage; A-11b added current inventory; Gate B added hosting/ownership fields without new tables. No Pipeline, Run, Schedule, native-history or sensitive-value storage is introduced. Review upgrades before applying them to an existing database; running the wrapper again is the supported idempotency check. This documentation review does not apply any migration.

SQL Server, SSISDB, and SSIS remain external to this stack and are not required for startup.

## Development OIDC path

A-06 imports the `flowplane` realm deterministically from `keycloak/flowplane-realm.json` when a fresh Keycloak development container starts. The import creates two separate public protocol representations:

- `flowplane-spa` uses Authorization Code with required S256 PKCE. Its default redirect URI is exactly `http://127.0.0.1:8080/oidc/callback`, its default web origin is exactly `http://127.0.0.1:8080`, and it has no client secret, implicit flow, password grant, or service account.
- `flowplane-api` is the resource identity. The SPA's access-token mapper adds `aud = flowplane-api`; the mapper deliberately does not add that audience to ID tokens.

The normal application remains same-origin at `http://127.0.0.1:8080`: Nginx serves the frontend and forwards `/api/me` to the protected backend `/me` endpoint. Keycloak remains a separate OIDC-provider origin at `http://127.0.0.1:8081`. The backend reads metadata inside the Compose network from `http://keycloak:8080/realms/flowplane/.well-known/openid-configuration`, but validates the exact public issuer `http://127.0.0.1:8081/realms/flowplane`. Keycloak's full `KC_HOSTNAME` URL and dynamic backchannel setting make the internal discovery document advertise that public issuer while exposing an internally reachable JWKS URI.

Plain HTTP metadata is an explicit development-only setting. `RequireHttpsMetadata` defaults to `true`, and the backend rejects `false` outside the ASP.NET Core `Development` environment. Production must supply HTTPS issuer and metadata configuration; the Compose stack explicitly selects `Development` because its loopback-only OIDC endpoints use HTTP.

The SPA stores both the oidc-client-ts user (including tokens) and the temporary authorization transaction/state in `sessionStorage`. It never falls back to `localStorage`, does not request `offline_access`, and does not configure persistent offline sessions. This is an MVP usability trade-off, not strong token isolation: any same-origin JavaScript can read `sessionStorage`. A BFF/token-isolation design remains an option if the FlowPlane threat model later requires it. A-06 adds no third-party analytics or scripts, and token lifetime remains controlled by the development realm (`accessTokenLifespan` is five minutes). Do not log browser tokens.

The example admin and browser-user passwords are local-development-only defaults. Override them in the ignored `.env`; never reuse them outside local development. Realm import is create-once for a Keycloak data state, so intentionally testing a changed import requires recreating that development container. The A-06 verification always uses a fresh isolated project and removes its data afterward:

```sh
sh tests/container/a06-oidc-path.sh
```

That verifier checks internal discovery and JWKS access, exact issuer validation through the live backend, a real browser login in `mcr.microsoft.com/playwright:v1.64.0-noble`, visible Authorization Code/S256 PKCE parameters, `flowplane-api` access-token audience, protected `/api/me`, lack of direct Vite/backend host bindings, and development credential/token redaction.

Run the A-04 topology regression and the A-05 PostgreSQL/readiness verification from the repository root:

```sh
sh tests/container/a04-development-stack.sh
sh tests/container/a05-postgresql-baseline.sh
```

After a foundation-wide change, run the thin Milestone 0 orchestrator. It invokes A-04 through A-07 in order, stops on the first failure, and leaves cleanup to each isolated contract:

```sh
sh tests/container/milestone0.sh
```

Stop the stack without deleting PostgreSQL data:

```sh
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml down
```
