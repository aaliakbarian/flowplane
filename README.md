# FlowPlane

FlowPlane is an early-stage open-source control plane for external data-pipeline platforms. It aims to provide a consistent place to discover, schedule, execute, and monitor pipelines while each provider retains its native runtime and provider-specific behavior.

## Status

FlowPlane is implementing an SSIS-first MVP and is not production-ready. The development foundation and secure SSIS configuration/discovery journey are implemented: a .NET backend, React/Vite frontend, Nginx browser entry point, PostgreSQL, Keycloak/OIDC, encrypted provider credentials, validation, durable asynchronous discovery and native-visibility-checked inventory browsing.

The [Milestone A live acceptance record](docs/development/milestones/milestone-a-live-acceptance.md) distinguishes real browser-to-SSISDB evidence from mocked tests and records completed implementation acceptance with GitHub closure pending. Pipeline registration, native SSIS execution, Control Flow visualization and recurring scheduling remain unimplemented.

## What FlowPlane is

FlowPlane is a provider-independent Control Plane over external Data-Pipeline Providers. SSIS is the first provider used to prove that boundary; future providers remain candidates rather than current commitments. SQL Server, SSISDB, and the SSIS runtime stay outside the FlowPlane Compose stack.

## Current MVP direction

The implemented slice authenticates through standard OIDC with development Keycloak and uses PostgreSQL for FlowPlane-owned state and current observed discovery inventory. An explicitly authorized operator can configure a provider, validate it, request a complete discovery refresh, recover its durable status after reload and browse eligible entry points. Later MVP slices cover registration, execution/monitoring, read-only Control Flow visualization and recurring scheduling; those remain direction, not current behavior.

## Architecture

FlowPlane separates its provider-independent Control Plane from provider-specific Execution Planes. Provider adapters translate FlowPlane operations into native platform behavior, while optional enterprise integrations remain additive rather than boot requirements. See the [architecture overview](docs/architecture/overview.md) and accepted [architecture decisions](docs/adr/README.md).

## Development quick start

The supported environment is WSL2 Ubuntu 24.04 with Git/SSH, the repository-pinned .NET 10 SDK, Docker Engine/CLI/Compose installed directly in WSL, and optionally VS Code through the WSL extension. Docker Desktop, a Dev Container, host Node.js, host PostgreSQL, and host Keycloak are not required. The verified checkout is `~/source_codes/flowplane`; another path inside the WSL filesystem is also supported.

Clone into the WSL filesystem and enter the repository:

```sh
git clone git@github.com:aaliakbarian/flowplane.git
cd flowplane
```

Create the ignored local Compose configuration and align container-written frontend files with your WSL identity:

```sh
cp deploy/compose/.env.example deploy/compose/.env
sed -i \
  -e "s/^FLOWPLANE_UID=.*/FLOWPLANE_UID=$(id -u)/" \
  -e "s/^FLOWPLANE_GID=.*/FLOWPLANE_GID=$(id -g)/" \
  deploy/compose/.env
```

Restore, build, and test the backend with the WSL-installed SDK:

```sh
dotnet restore FlowPlane.slnx --disable-parallel -m:1 --nologo
dotnet build FlowPlane.slnx --no-restore --nologo -m:1
dotnet test FlowPlane.slnx --no-restore --nologo -m:1
```

Start the normal development path:

```sh
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml up --build --wait
```

Open `http://127.0.0.1:8080`. Nginx serves the browser origin, sends `/api/*` to the backend, and sends other traffic to the containerized Vite server with HMR. Sign in through Keycloak using the development test identity configured in the ignored `deploy/compose/.env`; never reuse those local-only values outside development.

Apply the committed PostgreSQL migrations explicitly; the current schema has four migrations and seven public tables. The API does not migrate automatically on startup. The wrapper asks Docker Compose to parse `.env` as data, then invokes the repository-pinned EF tool without printing credentials:

```sh
sh deploy/compose/apply-migrations.sh
```

Verify current health semantics through Nginx:

```sh
curl --fail --silent --show-error http://127.0.0.1:8080/api/health/live
curl --fail --silent --show-error http://127.0.0.1:8080/api/health/ready
```

`/health/live` reports process/self health. `/health/ready` currently includes PostgreSQL readiness; Keycloak and SSIS are not backend readiness dependencies.

Stop the stack while preserving PostgreSQL data:

```sh
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml down
```

The foundation boots without SQL Server, SSISDB, or SSIS configuration.

For provider use, follow the [Compose secret/runtime guide](deploy/compose/README.md), [operator/endpoint API configuration](docs/development/a12-stage1-api.md), [discovery-hosting configuration](docs/development/a12-stage2-discovery.md#configuration-and-gate-b-operation) and [operator UI guide](docs/development/a12-stage4-ui.md). Login alone does not grant provider administration. Disposable local acceptance data does not require production disaster-recovery infrastructure; [production recovery requirements remain separate](docs/development/milestones/milestone-a-live-acceptance.md#disposable-development-data-and-deployment-readiness).

## Production-style frontend verification

The production-style path verifies `Vite build -> dist -> Nginx`, including static assets, SPA fallback, same-origin API routing, and OIDC. It is packaging verification, not production deployment guidance:

```sh
sh tests/container/a07-production-frontend.sh
```

See the [Compose development guide](deploy/compose/README.md) for the alternate profiled runtime command.

## Repository layout

- `src/backend` — .NET projects and API composition root.
- `src/frontend` — React, TypeScript, Vite, and browser tests.
- `tests` — architecture, integration, documentation, and container contracts.
- `deploy/compose` — development services, Nginx, and Keycloak bootstrap.
- `docs` — product, domain, architecture, ADR, and implementation guidance.

## Documentation

- [Documentation index](docs/README.md)
- [MVP scope](docs/product/mvp-scope.md)
- [Architecture overview](docs/architecture/overview.md)
- [MVP domain model](docs/domain/mvp-domain-model.md)
- [SSIS Provider Technical Design](docs/architecture/providers/ssis-provider-technical-design.md)
- [MVP implementation plan](docs/development/mvp-implementation-plan.md)
- [Milestone A execution plan](docs/development/milestones/milestone-a-foundation-and-ssis-discovery.md)
- [Milestone B technical and implementation plan](docs/development/milestones/milestone-b-first-durable-manual-ssis-execution.md)
- [ADR index](docs/adr/README.md)
- [Contributing guide](CONTRIBUTING.md)

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for contributor setup, verification tiers, and pull-request expectations.

## License

Apache License 2.0. See [LICENSE](LICENSE).
