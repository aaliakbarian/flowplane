# FlowPlane development Compose stack

`compose.yaml` is the single supported development Compose file. The name follows the current Docker Compose convention and avoids environment-specific file overlays before they are needed.

Copy `.env.example` to the ignored `.env` file, then set `FLOWPLANE_UID` and `FLOWPLANE_GID` to the values reported by `id -u` and `id -g` in WSL. All committed passwords are explicitly local-development-only defaults; replace them in `.env` when needed and never store real credentials in the example file.

From the repository root, validate and start the stack with:

```sh
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml config
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml up --build --wait
```

The normal browser entry point is `http://127.0.0.1:${FLOWPLANE_HTTP_PORT}` (port `8080` by default). Nginx strips the `/api` prefix when forwarding API requests, so `/api/health/live` and `/api/health/ready` reach the backend's existing `/health/live` and `/health/ready` endpoints. Frontend HTTP and Vite HMR/WebSocket traffic also traverse Nginx; neither Vite nor the backend publishes a host port.

PostgreSQL is available to WSL at `127.0.0.1:${FLOWPLANE_POSTGRES_PORT}` and stores PostgreSQL 18 data in the named volume mounted at `/var/lib/postgresql`. Keycloak development mode is available at `http://127.0.0.1:${FLOWPLANE_KEYCLOAK_PORT}`. A-04 does not create a realm, client, OIDC configuration, application schema, migration, or backend dependency health check.

As of A-05, backend readiness includes PostgreSQL connectivity while liveness remains process-only. Compose supplies separate `FlowPlane__PostgreSql__*` configuration values so Npgsql can safely construct the connection string even when credentials contain connection-string delimiters. Credentials are not stored in source configuration or written to logs.

## Apply the PostgreSQL baseline migration

Migrations are explicit and are not applied automatically when the application starts. From the repository root, restore the pinned local EF tool and apply migrations to the WSL-accessible PostgreSQL port:

```sh
set -a
. deploy/compose/.env
set +a

dotnet tool restore
FlowPlane__PostgreSql__Host=127.0.0.1 \
FlowPlane__PostgreSql__Port="${FLOWPLANE_POSTGRES_PORT}" \
FlowPlane__PostgreSql__Database="${FLOWPLANE_POSTGRES_DB}" \
FlowPlane__PostgreSql__Username="${FLOWPLANE_POSTGRES_USER}" \
FlowPlane__PostgreSql__Password="${FLOWPLANE_POSTGRES_PASSWORD}" \
  dotnet tool run dotnet-ef database update \
  --project src/backend/FlowPlane.Infrastructure/FlowPlane.Infrastructure.csproj \
  --startup-project src/backend/FlowPlane.Infrastructure/FlowPlane.Infrastructure.csproj
```

The pinned empty baseline migration creates only EF Core's `__EFMigrationsHistory` table. It deliberately creates no FlowPlane domain tables. Running the update command again is the supported idempotency check for the development bootstrap path.

SQL Server, SSISDB, and SSIS remain external to this stack and are not required for startup.

Run the A-04 topology regression and the A-05 PostgreSQL/readiness verification from the repository root:

```sh
sh tests/container/a04-development-stack.sh
sh tests/container/a05-postgresql-baseline.sh
```

Stop the stack without deleting PostgreSQL data:

```sh
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml down
```
