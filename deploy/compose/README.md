# FlowPlane development Compose stack

`compose.yaml` is the single supported development Compose file. The name follows the current Docker Compose convention and avoids environment-specific file overlays before they are needed.

Copy `.env.example` to the ignored `.env` file, then set `FLOWPLANE_UID` and `FLOWPLANE_GID` to the values reported by `id -u` and `id -g` in WSL. All committed passwords are explicitly local-development-only defaults; replace them in `.env` when needed and never store real credentials in the example file.

From the repository root, validate and start the stack with:

```sh
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml config
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml up --build --wait
```

The normal browser entry point is `http://127.0.0.1:${FLOWPLANE_HTTP_PORT}` (port `8080` by default). Nginx strips the `/api` prefix when forwarding API requests, so `/api/health/live` and `/api/health/ready` reach the backend's existing `/health/live` and `/health/ready` endpoints. Frontend HTTP and Vite HMR/WebSocket traffic also traverse Nginx; neither Vite nor the backend publishes a host port.

PostgreSQL is available to WSL at `127.0.0.1:${FLOWPLANE_POSTGRES_PORT}` and stores PostgreSQL 18 data in the named volume mounted at `/var/lib/postgresql`. Keycloak development mode is available at `http://127.0.0.1:${FLOWPLANE_KEYCLOAK_PORT}`. A-04 does not create a realm, client, OIDC configuration, application schema, migration, or backend dependency health check. Backend liveness and readiness intentionally retain their A-02 semantics until those dependencies are integrated in A-05 and A-06.

SQL Server, SSISDB, and SSIS remain external to this stack and are not required for startup.

Run the canonical full-stack verification from the repository root:

```sh
sh tests/container/a04-development-stack.sh
```

Stop the stack without deleting PostgreSQL data:

```sh
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml down
```
