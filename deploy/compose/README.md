# FlowPlane development Compose stack

`compose.yaml` is the single supported development Compose file. It provides two alternate frontend serving modes over the same backend, PostgreSQL, and Keycloak foundation: the default Vite/HMR development path and the opt-in production-style static Nginx path.

Copy `.env.example` to the ignored `.env` file, then set `FLOWPLANE_UID` and `FLOWPLANE_GID` to the values reported by `id -u` and `id -g` in WSL. The committed example contains local-development-only bootstrap values, not production or shared credentials. Database passwords, Keycloak bootstrap credentials, browser test credentials, and tokens are secret values even in development; override them only in the ignored `.env` and never commit or log them. Browser-facing OIDC authority and public client ID values are intentionally public configuration and must not be mistaken for secrets.

From the repository root, validate and start the stack with:

```sh
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml config
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml up --build --wait
```

The normal browser entry point is `http://127.0.0.1:${FLOWPLANE_HTTP_PORT}` (port `8080` by default). Nginx strips the `/api` prefix when forwarding API requests, so `/api/health/live` and `/api/health/ready` reach the backend's existing `/health/live` and `/health/ready` endpoints. Frontend HTTP and Vite HMR/WebSocket traffic also traverse Nginx; neither Vite nor the backend publishes a host port.

PostgreSQL is available to WSL at `127.0.0.1:${FLOWPLANE_POSTGRES_PORT}` and stores PostgreSQL 18 data in the named volume mounted at `/var/lib/postgresql`. Keycloak development mode is available at `http://127.0.0.1:${FLOWPLANE_KEYCLOAK_PORT}`.

## Production-style frontend path

Stop the default development stack before using the alternate production-style frontend because both modes intentionally use the same FlowPlane browser port. Then start the targeted profiled service from the repository root:

```sh
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml down
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml --profile production up --build --wait frontend-production
```

Targeting `frontend-production` is deliberate. Compose starts only that service and its backend, PostgreSQL, and Keycloak dependencies; it does not start the development `nginx` or `frontend-dev` services, so there is no host-port collision. The browser origin remains `http://127.0.0.1:${FLOWPLANE_HTTP_PORT}` (`8080` by default), while Keycloak remains at port `8081` by default.

The production target builds React/TypeScript with Vite, copies `dist/` into `nginx:1.30.5-alpine3.24`, serves hashed assets directly, falls back to `index.html` for non-API SPA paths such as `/oidc/callback`, and preserves the same `/api/` prefix-stripping proxy behavior as development. It does not contain or run Node.js or Vite. The OIDC authority and public client ID are build-time browser configuration, not secrets; a different public authority requires rebuilding this static image.

Run the bounded production-path verification with:

```sh
sh tests/container/a07-production-frontend.sh
```

As of A-05, backend readiness includes PostgreSQL connectivity while liveness remains process-only. Compose supplies separate `FlowPlane__PostgreSql__*` configuration values so Npgsql can safely construct the connection string even when credentials contain connection-string delimiters. Credentials are not stored in source configuration or written to logs.

## Apply the PostgreSQL baseline migration

Migrations are explicit and are not applied automatically when the application starts. From the repository root, use the wrapper to resolve the ignored Compose environment file as data, restore the pinned local EF tool, and apply migrations to the WSL-accessible PostgreSQL port:

```sh
sh deploy/compose/apply-migrations.sh
```

The wrapper does not source `.env` as shell code and does not print the resolved password. Pass a different environment-file path as its only argument when needed. The pinned empty baseline migration creates only EF Core's `__EFMigrationsHistory` table. It deliberately creates no FlowPlane domain tables. Running the wrapper again is the supported idempotency check for the development bootstrap path.

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
