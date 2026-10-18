# Contributing to FlowPlane

FlowPlane is early-stage. Keep changes bounded to the current milestone or issue, preserve the provider-independent core, and include RED/GREEN evidence appropriate to the behavior being changed.

## Supported development environment

The supported contributor baseline is WSL2 Ubuntu 24.04 with Git/SSH, .NET 10, Docker Engine/CLI/Compose installed directly in WSL, and VS Code through the WSL extension when desired. Docker Desktop and a Dev Container are not required. Node.js, npm, TypeScript, Vite, PostgreSQL, and Keycloak do not need to be installed on the WSL host.

Repository source stays in the WSL filesystem. Frontend tooling and service dependencies run in containers; backend restore, build, tests, and debugging use the repository-pinned WSL .NET SDK.

`global.json` is the authoritative minimum SDK policy. Install SDK `10.0.113` or a compatible later .NET 10 feature band directly in WSL before using the native backend workflow, and confirm it with `dotnet --version`. If Ubuntu's configured package sources have not yet published a compatible serviced SDK, use `mcr.microsoft.com/dotnet/sdk:10.0.402-noble` for interim verification; do not treat native WSL verification as complete until the required SDK is installed. The container is an interim toolchain path, not a Dev Container, and Docker Desktop remains unnecessary.

## Contributor setup

Follow the [root quick start](README.md#development-quick-start) to create the ignored `deploy/compose/.env`, build and test the backend, start the development stack, apply the baseline migration, and verify health. Configuration in `.env.example` is local-development-only. Treat database passwords, Keycloak bootstrap credentials, browser test credentials, and tokens as secrets even in development; do not commit or log overrides.

The normal browser path is:

```text
Browser -> Nginx -> /api/* -> backend
                 -> /*     -> Vite/HMR
```

The alternate production-style verification path is:

```text
Browser -> static Nginx -> /api/* -> backend
                        -> /*     -> compiled dist
```

The second path verifies packaging behavior; it is not a production deployment architecture.

## Verification tiers

Run backend checks from WSL:

```sh
dotnet restore FlowPlane.slnx --disable-parallel -m:1 --nologo
dotnet format FlowPlane.slnx --verify-no-changes --no-restore
dotnet build FlowPlane.slnx --no-restore --nologo -m:1
dotnet test FlowPlane.slnx --no-restore --nologo -m:1
```

Build the reusable frontend development image and run frontend checks entirely in containers:

```sh
docker build \
  --file src/frontend/Dockerfile \
  --target development \
  --build-arg USER_UID="$(id -u)" \
  --build-arg USER_GID="$(id -g)" \
  --tag flowplane-frontend-dev:local \
  .

docker run --rm --mount type=bind,source="$PWD/src/frontend",target=/workspace/src/frontend flowplane-frontend-dev:local npm run typecheck
docker run --rm --mount type=bind,source="$PWD/src/frontend",target=/workspace/src/frontend flowplane-frontend-dev:local npm test
docker run --rm --mount type=bind,source="$PWD/src/frontend",target=/workspace/src/frontend flowplane-frontend-dev:local npm run build
```

Run lightweight repository contracts before every pull request:

```sh
sh tests/documentation/foundation-contract.sh
find tests -type f -name '*.sh' -exec sh -n {} \;
docker compose -f deploy/compose/compose.yaml config --quiet
git diff --check
```

The heavier container contracts are independently runnable:

```sh
sh tests/container/a04-development-stack.sh
sh tests/container/a05-postgresql-baseline.sh
sh tests/container/a06-oidc-path.sh
sh tests/container/a07-production-frontend.sh
```

Run the complete Milestone 0 foundation matrix with the thin aggregate command:

```sh
sh tests/container/milestone0.sh
```

Each contract owns its isolated Compose project and cleanup. The aggregate stops at the first failure and does not duplicate or broaden the contracts' volume-removal behavior.

Required GitHub CI runs the backend, frontend, container-image, PostgreSQL, and documentation jobs on Ubuntu 24.04. The browser-heavy A-04, A-06, and A-07 journeys remain explicit local/integration gates rather than being repeated on every push.

## Clean-checkout expectations

A contribution must remain reproducible without ignored or generated state. Do not track local `.env` files, `bin`, `obj`, `node_modules`, or `dist`. Rebuilding application containers must not remove WSL source, and ordinary container restarts must preserve the PostgreSQL named volume.

Every documented command changed by a pull request should be exercised from a Git-derived clean tree containing only repository files and the proposed patch.

## Pull requests

- Link the issue or milestone task and state the bounded scope.
- Include meaningful RED evidence and the final GREEN commands/results.
- Note migrations, configuration, security-sensitive behavior, and intentionally deferred checks.
- Keep repository-development CI distinct from FlowPlane's possible future product-level CI/CD integrations.
- Do not add deployment, release, package-publication, or unapproved infrastructure workflows as incidental cleanup.

## SSIS integration work

SQL Server, SSISDB, and SSIS remain external to the Compose stack and are not required for ordinary foundation development or CI. A-10 and later provider work will require separately configured, representative real-SSISDB evidence. Do not silently replace that future gate with mocks, and do not add provider placeholders to foundation-only changes.
