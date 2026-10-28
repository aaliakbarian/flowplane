# Milestone A — Development Foundation + SSIS Discovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` or `superpowers:executing-plans` to implement this plan task by task. Do not begin a task until its prerequisites and review gate are satisfied.

**Goal:** From a clean checkout, run the FlowPlane development foundation, authenticate through Keycloak/OIDC, configure and validate an external SSIS `ProviderInstance`, and discover executable SSIS packages without registering or executing them.

**Architecture:** Milestone A completes the repository and development foundation before adding the narrow provider contracts required for SSIS validation and discovery. FlowPlane's provider-independent projects depend inward, SSIS-specific behavior remains in `FlowPlane.Providers.Ssis`, and Nginx is the single browser-facing entry point for both development and production-style verification.

**Tech stack:** .NET 10, ASP.NET Core, React 19, TypeScript 6, Vite 8, Nginx, PostgreSQL 18, Keycloak 26.8, Docker Engine and Docker Compose in WSL2, Microsoft.Data.SqlClient, xUnit, Vitest, React Testing Library, and Playwright.

**Spec:** This plan refines the [FlowPlane MVP Implementation Plan](../mvp-implementation-plan.md), especially Milestone 0, Slice 1, the Definition of Done, and the implementation gates.

## Purpose

This file is the source-controlled implementation execution plan for **Milestone A — Development Foundation + SSIS Discovery**. It breaks the milestone into stable, reviewable tasks with explicit RED/GREEN verification and exit criteria.

This plan refines, but does not replace, redefine, or supersede the [FlowPlane MVP Implementation Plan](../mvp-implementation-plan.md). Accepted Architecture Decision Records, the MVP implementation plan, the MVP domain model, and the SSIS Provider Technical Design remain authoritative for their respective decisions. Where this plan records an implementation recommendation rather than accepted architecture, it labels that recommendation accordingly.

Creating or updating this document does not authorize implementation. Every task initially remains `Planned`; A-01 begins only after an explicit implementation instruction.

## Milestone objective

From a clean checkout:

- run the FlowPlane development foundation;
- authenticate through Keycloak/OIDC;
- configure an external SSIS `ProviderInstance`;
- validate connectivity and capability availability; and
- discover executable SSIS packages.

SSIS execution, `Pipeline` registration, `Run`/`RunAttempt`, scheduling, graph visualization, deployment, and Data Flow are outside Milestone A.

## Progress tracking

The task sections are the authoritative detail. This checklist records source-controlled implementation progress for navigation; the GitHub issues and Milestone remain the live review/status tracker.

Closure review note (2026-10-28): the [live acceptance and source evidence](milestone-a-live-acceptance.md) confirms the original product exit criteria for A-10, A-11 and A-12, including the closed A-11a/A-11b issues. All twelve tasks are Complete and checked. A-12 issue #13, parent #1 and the GitHub milestone remain open pending explicit closure after documentation review and Foundation CI; this reconciliation authorizes no new implementation or issue closure.

- [x] A-01 Minimum development foundation
- [x] A-02 Minimal backend host and container path
- [x] A-03 Containerized frontend development foundation
- [x] A-04 Nginx-fronted development Compose stack
- [x] A-05 PostgreSQL baseline and truthful readiness
- [x] A-06 Minimal Keycloak/OIDC path
- [x] A-07 Production-style frontend path
- [x] A-08 Milestone 0 closure
- [x] A-09 Minimal provider configuration and secret boundary
- [x] A-10 SSIS connectivity and capability validation
- [x] A-11 Read-only SSIS discovery
- [x] A-12 Thin configuration/discovery API and UI

## Environment baseline

The following development environment was verified before this plan was created:

- WSL2 Ubuntu 24.04;
- repository at `~/source_codes/flowplane`;
- .NET SDK 10.0.112;
- Docker Engine 29.7.2;
- Docker Compose v5.5.0;
- VS Code connected through the WSL extension;
- Docker Desktop is not required;
- no VS Code Dev Container or workspace container;
- frontend Node.js/Vite tooling runs in containers;
- Nginx is browser-facing;
- PostgreSQL and Keycloak run in containers; and
- SQL Server, SSISDB, and SSIS remain external to the FlowPlane Compose stack.

The observed patch versions are not permanent product support promises. Repository configuration will pin required SDK, package, and image versions during implementation, and support claims require the applicable compatibility evidence.

## Implementation gate decisions

Unless identified as an accepted architectural constraint, the choices below are **implementation recommendations for Milestone A**. They remain subordinate to accepted ADRs and may be revised through the appropriate review when implementation evidence requires it.

- **Backend project layout — implementation recommendation:** begin with five logical projects: `FlowPlane.Domain`, `FlowPlane.Application`, `FlowPlane.Infrastructure`, `FlowPlane.Providers.Ssis`, and `FlowPlane.Api`. Combine or split projects only when a concrete boundary or testing need justifies it.
- **SDK pin — implementation recommendation:** use `global.json` with serviced .NET SDK `10.0.113`, `rollForward` set to `latestFeature`, and `allowPrerelease` set to `false`. A-02 refined the A-01 `latestPatch` setting because the supported Ubuntu package and tested official .NET 10 SDK container may occupy different feature bands while both remain within .NET 10. The October 2026 security-servicing update raised the minimum patch from `10.0.112` to `10.0.113`; the tested container uses SDK `10.0.402`.
- **Frontend baseline — implementation recommendation:** React 19, TypeScript 6, and Vite 8, with exact compatible package versions and a lockfile selected during scaffolding. Node.js and frontend tooling remain containerized.
- **Browser entry — development constraint for this milestone:** Nginx is the browser-facing entry point in development and production-style verification. The browser does not connect directly to Vite.
- **PostgreSQL baseline — implementation recommendation:** PostgreSQL 18, pinned to a tested patch/image digest when Compose configuration is created.
- **Persistence migrations — implementation recommendation:** use FlowPlane-owned EF Core migrations while the preferred EF Core persistence direction remains suitable.
- **Identity baseline — implementation recommendation:** use a tested Keycloak 26.8 image through standard OIDC/OAuth2 behavior without coupling application logic to Keycloak-specific APIs.
- **Backend test stack — implementation recommendation:** xUnit, plus ASP.NET Core integration-test support where required.
- **Frontend test stack — implementation recommendation:** Vitest and React Testing Library.
- **Browser test stack — implementation recommendation:** Playwright for a limited set of critical browser journeys.
- **Provider secrets — accepted ADR-0006 refinement:** `flowplane:` is the normal built-in encrypted store, with PostgreSQL-persisted Data Protection keys explicitly protected by external X.509 root material. `env:` remains development/testing/bootstrap-only. Never persist plaintext credentials or expose locators through the API.
- **Portable SSIS authentication baseline — implementation recommendation:** use a dedicated least-privilege SQL login over an encrypted Microsoft.Data.SqlClient connection. Any development certificate-trust exception must be explicit and must not become the production default.
- **Background work — approved A-12 refinement:** one discovery-specific BackgroundService in the existing backend executes already accepted, durably fenced PostgreSQL refreshes. This does not select a workflow scheduler, external queue or native execution worker; see the [Stage 2 contract](../a12-stage2-discovery.md).

Exact package patches and image digests are local implementation choices that must be verified and recorded in the first task that uses them. They are not hidden architecture decisions or permanent support promises.

## Dependency rules for A-01

- `FlowPlane.Domain` has no FlowPlane project dependencies.
- `FlowPlane.Application` may depend on `FlowPlane.Domain`.
- `FlowPlane.Infrastructure` may depend inward on `FlowPlane.Application` and `FlowPlane.Domain`.
- `FlowPlane.Providers.Ssis` may depend inward on `FlowPlane.Application` and `FlowPlane.Domain` only when required by implemented behavior.
- `FlowPlane.Application` and `FlowPlane.Domain` must not reference `FlowPlane.Providers.Ssis`.
- `FlowPlane.Api` is the future composition root and may reference the projects it must compose.
- Do not define provider interfaces, placeholder domain concepts, or speculative abstractions merely to populate empty projects.

These rules protect the provider-independent core without treating the project layout as permanent process or deployment topology.

## Global constraints

- Implement tasks in task-ID order unless an explicit review changes a dependency.
- Each task begins with its stated RED evidence and ends only after its GREEN evidence and exit criteria are satisfied.
- A task is not complete merely because code compiles; applicable tests, documentation, security checks, and observable behavior are required.
- Use the WSL-installed .NET SDK for the primary backend loop; keep frontend tooling and product dependencies containerized.
- Keep Nginx as the browser-facing entry point and preserve one browser origin for frontend and API traffic.
- Keep SQL Server, SSISDB, and SSIS external to the Compose stack.
- Ordinary build and unit-test workflows must not require SSIS access.
- Real SSISDB evidence is mandatory before A-10, A-11, or Milestone A can be declared complete.
- Do not commit real credentials, tokens, connection strings, private production packages, or sensitive SSIS metadata.
- Stop and use the architecture process if implementation pressure would change an accepted boundary.

## Review focus

The following failure modes require explicit attention during task review:

1. **Provider-boundary leakage:** A-01 and A-09 must prove that Domain and Application do not acquire SSIS-specific dependencies or vocabulary.
2. **Hidden SSIS boot dependency:** A-04 and A-08 must prove that the normal FlowPlane stack starts without SSIS configuration or connectivity.
3. **Disposable-container data loss:** A-04, A-05, and A-08 must prove that rebuilding application containers preserves WSL source and ordinary container restarts preserve PostgreSQL state.
4. **Credential disclosure:** A-09, A-10, and A-12 must test configuration binding, persistence, structured logs, exceptions, and API/UI errors for secret leakage.
5. **Incorrect discovery eligibility:** A-11 and A-12 must test permission-filtered hierarchy, entry-point filtering, duplicate native names in different paths, and the separation of discovery from registration.

---

## A-01 — Minimum development foundation

**Status:** Complete

### Goal and scope

Create only the minimum .NET development foundation needed by later work:

- repository SDK pinning;
- a solution containing the five logical backend projects;
- shared build and dependency-version configuration only where immediately useful;
- minimal backend unit/architecture test infrastructure; and
- project references that enforce the dependency rules in this plan.

This is the first coding task and first implementation commit. Its commit must contain foundation work only.

### Dependencies

- Approved Milestone A execution plan.
- Accepted ADRs and current MVP/provider technical designs.
- Verified WSL-installed .NET SDK 10.0.112.

### Explicit non-goals

- HTTP endpoints or application behavior;
- Dockerfiles or Compose services;
- frontend scaffolding;
- database or EF Core configuration;
- authentication;
- provider contracts or SSIS code; and
- placeholder interfaces or domain types created only to populate projects.

### Conceptually affected areas

- `global.json`;
- solution and shared .NET build configuration;
- `src/backend/FlowPlane.Domain/`;
- `src/backend/FlowPlane.Application/`;
- `src/backend/FlowPlane.Infrastructure/`;
- `src/backend/FlowPlane.Providers.Ssis/`;
- `src/backend/FlowPlane.Api/`; and
- baseline backend tests under `tests/unit/`.

### RED verification

- Repository-pinned SDK resolution is absent because `global.json` does not exist.
- Solution restore, build, and test commands cannot run because no solution exists.
- No automated assertion protects the intended project dependency direction.

### GREEN verification

- `dotnet --version` resolves SDK 10.0.112 through repository configuration.
- A-01 originally verified `rollForward: latestPatch`; A-02 later refined the repository policy to `latestFeature` so the WSL 10.0.1xx SDK and official container 10.0.4xx SDK feature bands are both supported. The current repository policy is recorded in the Implementation gate decisions section above.
- `dotnet restore`, `dotnet build`, and `dotnet test` succeed from WSL.
- Architecture tests prove that Domain has no FlowPlane project dependency, Application does not reference the SSIS provider, and neither Domain nor Application references `FlowPlane.Providers.Ssis`.
- The diff contains no HTTP behavior, containers, frontend, persistence, authentication, or provider implementation.

### Exit criteria

The pinned SDK, minimal solution, five project boundaries, and dependency-direction verification work from the WSL repository. A-01 remains limited to development foundation and is independently reviewable.

## A-02 — Minimal backend host and container path

**Status:** Complete

### Goal and scope

Create the smallest ASP.NET Core host needed for subsequent stack work, including structured JSON console logging, separate liveness/readiness endpoints, host-level integration tests, and a multi-stage backend container build/runtime path.

### Dependencies

- A-01 complete and reviewed.

### Explicit non-goals

- PostgreSQL migrations or readiness checks;
- OIDC authentication;
- provider configuration or SSIS behavior;
- background workers; and
- public product APIs beyond health behavior.

### Conceptually affected areas

- `FlowPlane.Api` composition root;
- backend health and logging configuration;
- backend API integration tests; and
- backend Docker build/runtime files.

### RED verification

- Health integration tests fail because no host endpoints exist.
- The backend container image cannot be built or started.

### GREEN verification

- Host-level API tests pass for liveness and readiness semantics.
- The backend image builds and starts successfully.
- `/health/live` and `/health/ready` return the documented minimal results.
- Console logs are structured and do not expose configuration secrets.

### Exit criteria

The minimal backend can be built, tested, run directly with the WSL SDK, and built/run as a container without introducing database, identity, or provider behavior.

## A-03 — Containerized frontend development foundation

**Status:** Complete

### Goal and scope

Scaffold the minimal React/TypeScript/Vite frontend, strict type checking, Vitest and React Testing Library, a dependency lockfile, and a containerized development/build toolchain. Frontend source remains in the WSL working tree and is bind-mounted for development.

### Dependencies

- A-01 complete and reviewed.
- Exact compatible React, TypeScript, Vite, Node.js image, and test-package versions selected and recorded.

### Explicit non-goals

- Provider configuration or discovery screens;
- authentication flows;
- React Flow;
- product component-library selection; and
- host installation of Node.js or frontend package managers.

### Conceptually affected areas

- `src/frontend/`;
- frontend dependency manifest and lockfile;
- TypeScript, Vite, and unit-test configuration;
- frontend development container configuration; and
- baseline frontend tests.

### RED verification

- Containerized frontend install, type-check, test, and build commands are unavailable.
- No frontend smoke test can run.

### GREEN verification

- Clean dependency installation from the lockfile succeeds inside the frontend container.
- Type checking, unit tests, and the production build pass inside the container.
- A minimal smoke component renders under the frontend test runner.
- The workflow requires no host Node.js installation.

### Exit criteria

The frontend has a reproducible containerized development and build foundation with WSL-hosted source, strict typing, a lockfile, and a working test runner.

## A-04 — Nginx-fronted development Compose stack

**Status:** Complete

### Goal and scope

Create the normal development stack with Nginx, frontend development, backend runtime, PostgreSQL, and Keycloak services. Nginx routes `/api/*` to the backend and proxies normal frontend and HMR/WebSocket traffic to Vite. Add health checks, readiness-aware startup, named PostgreSQL storage, example environment configuration, and ignored local overrides.

### Dependencies

- A-02 complete and reviewed.
- A-03 complete and reviewed.
- Exact tested Nginx, PostgreSQL, and Keycloak image patches/digests selected and recorded.

### Explicit non-goals

- a workspace or Dev Container service;
- Docker Desktop support as a requirement;
- SQL Server, SSISDB, or SSIS containers;
- complete Keycloak realm/client behavior; and
- production deployment topology.

### Conceptually affected areas

- `deploy/compose/`;
- development Nginx configuration;
- service health checks and dependency readiness;
- environment examples and ignored local overrides; and
- Compose smoke/integration verification.

### RED verification

- No Compose configuration can be validated or started.
- Same-origin API routing and Vite HMR/WebSocket proxying through Nginx cannot be demonstrated.

### GREEN verification

- `docker compose config` succeeds and the development stack reaches its expected health state.
- The browser reaches frontend and API traffic through Nginx rather than directly through Vite.
- Vite HMR/WebSocket traffic traverses Nginx.
- PostgreSQL and Keycloak start from pinned images.
- The stack boots with no SSIS configuration.
- Removing and rebuilding application containers does not remove WSL source changes.

### Exit criteria

The Nginx-fronted development topology starts reproducibly in WSL without Docker Desktop, a workspace container, or any SSIS dependency.

## A-05 — PostgreSQL baseline and truthful readiness

**Status:** Complete

### Goal and scope

Add baseline PostgreSQL connectivity and FlowPlane-owned EF Core migration infrastructure. Make backend readiness depend truthfully on PostgreSQL while liveness continues to describe process health. Verify migration bootstrap and development-data persistence.

### Dependencies

- A-04 complete and reviewed.
- EF Core migration recommendation confirmed for the initial persistence path.

### Explicit non-goals

- `Pipeline`, `Run`, `RunAttempt`, schedule, or execution schemas;
- a complete future database design;
- provider credential persistence; and
- use of PostgreSQL as authority for provider-native facts.

### Conceptually affected areas

- `FlowPlane.Infrastructure` database integration;
- baseline EF Core context and migration ownership;
- backend readiness behavior;
- PostgreSQL integration tests; and
- Compose persistence verification.

### RED verification

- Readiness does not distinguish an unavailable PostgreSQL dependency.
- No reproducible migration/bootstrap path exists.
- PostgreSQL persistence across ordinary container restarts is unverified.

### GREEN verification

- The baseline migration applies to a new database and can be verified repeatably.
- Backend readiness becomes unhealthy when PostgreSQL is unavailable while liveness remains truthful.
- PostgreSQL development state survives ordinary FlowPlane application-container restarts.
- Schema review confirms that no deferred Milestone A concepts were introduced.

### Exit criteria

FlowPlane has a minimal, reproducible PostgreSQL migration path and truthful health behavior without prematurely designing persistence for later milestones.

## A-06 — Minimal Keycloak/OIDC path

**Status:** Complete

### Goal and scope

Add deterministic development realm/client bootstrap, standards-based API token validation, and a minimal frontend authorization-code-with-PKCE login flow. Protect one minimal identity endpoint and keep browser/API traffic on the Nginx origin.

### Dependencies

- A-04 complete and reviewed.
- A-05 complete and reviewed.
- Tested Keycloak 26.8 patch/image digest selected and recorded.

### Explicit non-goals

- final RBAC or authorization architecture;
- copying the Keycloak user model into FlowPlane;
- provider authentication with Keycloak credentials; and
- Keycloak-specific application APIs.

### Conceptually affected areas

- Keycloak development realm/client bootstrap;
- backend OIDC/JWT validation;
- minimal frontend authentication state and redirect handling;
- protected identity endpoint; and
- backend/browser authentication tests.

### RED verification

- Unauthenticated access to the protected endpoint is not rejected.
- No browser authentication journey exists through the Nginx origin.
- Invalid issuer, audience, or expired token behavior is unverified.

### GREEN verification

- Unauthenticated protected requests return `401`.
- A user can authenticate through Keycloak and call the protected API through Nginx.
- Negative tests cover invalid issuer, invalid audience, and expired tokens.
- Tokens, client secrets, and Keycloak credentials do not appear in application logs.

### Exit criteria

Minimal standards-based OIDC authentication works end to end through the supported Nginx-fronted development environment without defining final RBAC.

## A-07 — Production-style frontend path

**Status:** Complete

### Goal and scope

Add a multi-stage frontend build in which Vite creates `dist/` and the final Nginx runtime serves compiled assets, proxies `/api/*`, and falls back to `index.html` for direct client-side routes.

### Dependencies

- A-02 complete and reviewed.
- A-03 complete and reviewed.
- A-04 complete and reviewed.

### Explicit non-goals

- production orchestration or hosting topology;
- Kubernetes;
- using the Vite development server in production; and
- product feature UI.

### Conceptually affected areas

- frontend multi-stage container build;
- production-style Nginx configuration;
- production-style Compose or verification profile; and
- SPA/API browser tests.

### RED verification

- Direct SPA navigation fails or returns an Nginx 404.
- The production-style runtime depends on the Vite development server or Node.js.
- Same-origin API routing in the production-style path is unverified.

### GREEN verification

- The production-style frontend image builds successfully.
- Direct client-side routes return the SPA entry point.
- `/api/*` remains same-origin through Nginx.
- The final runtime contains Nginx and compiled assets and does not require Node.js.

### Exit criteria

Vite production output is served correctly by a Node.js-free Nginx runtime with SPA fallback and same-origin API routing.

## A-08 — Milestone 0 closure

**Status:** Complete

### Goal and scope

Close Milestone 0 by adding repository-development CI and concise clean-checkout WSL documentation, then execute the complete foundation verification matrix. CI covers backend restore/build/test, containerized frontend install/type-check/test/build, formatting or lint checks, backend and production-style frontend image builds, migration bootstrap, and practical documentation checks.

### Dependencies

- A-01 through A-07 complete and reviewed.

### Explicit non-goals

- product-level CI/CD Integration;
- mandatory real-SSISDB tests in the ordinary contributor pipeline;
- deployment automation; and
- additional runtime infrastructure.

### Conceptually affected areas

- repository CI workflows;
- clean-checkout development documentation;
- local verification entry points; and
- Milestone 0 acceptance evidence.

### RED verification

- No reproducible clean-checkout procedure or CI path covers the foundation.
- Source persistence, PostgreSQL persistence, same-origin routing, OIDC, HMR, and production-style serving have not been verified together.

### GREEN verification

- Local equivalents of every CI stage pass from the supported WSL environment.
- A clean checkout reproduces backend, frontend, Compose, OIDC, health, migration, and production-style checks without host Node.js.
- Rebuilding application containers preserves WSL source; restarting application containers preserves PostgreSQL state.
- FlowPlane starts without SSIS configuration.
- CI reports the external SSIS suite as optional/separately configured rather than silently skipping a required provider gate.

### Exit criteria

Every Milestone 0 exit criterion in the MVP implementation plan has current evidence, applicable documentation is concise, and provider work can begin without expanding the foundation.

### Closure evidence

The GitHub-hosted `.github/workflows/foundation.yml` workflow completed successfully after the A-08 push, and the local clean-checkout Milestone 0 aggregate also passed. The root `README.md` and `CONTRIBUTING.md` document the verified WSL workflow, including that the FlowPlane foundation boots without SSIS configuration. GitHub issue #9 and the Milestone remain the live status tracker until issue #9 is formally closed.

## A-09 — Minimal provider configuration and secret boundary

**Status:** Complete

### Goal and scope

Add only the provider-independent concepts and contracts required to configure a provider instance, validate it, and request discovery. Keep SSIS-specific configuration behind the provider boundary. Under Accepted ADR-0006, ProviderInstance persists non-secret configuration and SecretReference only; the separate built-in store persists encrypted credentials and an externally protected Data Protection key ring. Environment resolution remains a limited development/testing/bootstrap path, not the default credential store.

Before implementation, perform the narrow Slice 1 API, persistence, and security checkpoint required by the MVP implementation plan.

### Dependencies

- A-08 complete and reviewed.
- Slice 1 API/persistence/security checkpoint reviewed.
- Development secret-resolution approach confirmed.

### Explicit non-goals

- provider plugin loading or a provider SDK;
- execution, cancellation, or observation contracts;
- `Pipeline` registration;
- future capability interfaces;
- raw connection-string persistence; and
- enterprise secret-manager integration.

### Conceptually affected areas

- provider-instance domain/application behavior;
- validation and discovery application contracts;
- SSIS-specific provider configuration;
- built-in managed-secret and limited environment `SecretReference` resolution;
- provider-instance, encrypted-secret and protected key-ring persistence/migration; and
- security and architecture tests.

### RED verification

- Provider configuration cannot be represented without leaking SSIS-specific details into the provider-independent projects.
- Tests demonstrate that raw credentials or connection strings could be persisted, serialized, returned, or logged.
- No application contract exists for validation and discovery without execution methods.

### GREEN verification

- Domain and Application remain free of SSIS-specific hierarchy and SqlClient dependencies.
- The provider contracts expose only validation and discovery behavior required by Slice 1.
- Provider-instance persistence contains a secret reference and non-secret configuration, never the resolved credential.
- Redaction tests cover configuration binding, persistence, exceptions, structured logs, and outward-facing errors.
- Migration review confirms that only Slice 1 provider-instance state is added.

### Exit criteria

FlowPlane can represent and persist a configured provider instance safely and resolve its development credential through a non-secret reference, with no execution or registration concepts introduced.

## A-10 — SSIS connectivity and capability validation

**Status:** Complete

### Goal and scope

Implement SSIS provider connectivity through Microsoft.Data.SqlClient and the public SSISDB catalog surface. Validate endpoint reachability, authentication, SSISDB availability, supported catalog/version facts, and effective baseline permissions as distinct outcomes. Return structured, sanitized provider errors.

### Dependencies

- A-09 complete and reviewed.
- Security checkpoint completed for the first real provider credential.
- Access to a supported real SQL Server/SSISDB test environment and least-privilege test principals.
- Exact Microsoft.Data.SqlClient version selected and recorded.

### Explicit non-goals

- SSIS execution or cancellation;
- package discovery result mapping beyond the probes required for validation;
- ISPAC retrieval;
- private SSISDB tables or undocumented procedures; and
- administrative `ssis_admin`/`sysadmin` as the normal runtime principal.

### Conceptually affected areas

- `FlowPlane.Providers.Ssis` connectivity and validation behavior;
- SqlClient configuration and TLS handling;
- structured provider error categories;
- unit tests with controlled failures; and
- opt-in real-SSISDB integration tests.

### RED verification

- Tests fail to distinguish unreachable server, invalid credentials, missing SSISDB, insufficient permissions, and unsupported compatibility facts.
- A secret or connection string can appear in an exception, structured log, or returned error.
- A reachable SQL Server is incorrectly treated as proof that SSIS discovery is available.

### GREEN verification

- Unit tests classify validation outcomes without requiring live SSIS access.
- Real-SSISDB tests distinguish reachability, authentication, catalog presence, compatibility, and effective permission failures.
- Production connection settings require encryption and certificate validation; any development trust exception is explicit.
- Logs and returned errors contain no passwords, access tokens, or complete connection strings.
- Capability availability is reported separately from basic connectivity.

### Exit criteria

The Linux/container-compatible backend validates a supported external SSISDB connection safely and reports actionable, structured capability results against a representative real environment.

## A-11 — Read-only SSIS discovery

**Status:** Complete

### Goal and scope

Scan every currently visible SSISDB folder/project and all approved current package, parameter and environment-reference metadata through public catalog views. Use internal project batches (`MaxProjectsPerBatch`, tentatively 20) and bounded streaming buffers with backpressure, without SSISDB result pagination, a fixed total catalog-size limit or parameter/reference truncation. Preserve native identities and current active revision observations; only compatible entry-point packages become executable candidates. Unknown-format definitions remain observed metadata with explicit candidate ineligibility.

Persist the current observations in FlowPlane PostgreSQL using unpublished staging generations and atomic publication after complete scan verification. SSISDB remains authoritative, and a failed/interrupted refresh retains the previous complete inventory. Scan completeness is separate from definition compatibility. Discovery remains read-only against SSISDB, creates no Pipeline and retrieves no ISPAC.

This approved refinement supersedes the earlier total-result bounds and inventory-persistence exclusion. It is decomposed under parent [issue #12](https://github.com/aaliakbarian/flowplane/issues/12); the detailed [A-11a/A-11b implementation plan](a11-discovery-implementation-plan.md) records RED/GREEN steps and the existing-work preservation review. Both children and parent #12 are closed following implementation and representative real acceptance; the [live acceptance record](milestone-a-live-acceptance.md) links the committed evidence. GitHub remains the live tracker.

| Child task | Status | Dependency | Deliverable |
| --- | --- | --- | --- |
| [A-11a / #14 — Complete read-only SSIS scan](https://github.com/aaliakbarian/flowplane/issues/14) | Complete | A-10 and implementation approval | Complete streaming observations and terminal scan summary |
| [A-11b / #15 — Current discovery inventory publication](https://github.com/aaliakbarian/flowplane/issues/15) | Complete | Reviewed A-11a contract and acceptance; migration/security review | Durable credential-scoped inventory with atomic publication |

The order is A-10 -> A-11a -> A-11b -> parent A-11 acceptance -> A-12. This documentation/issue reconciliation authorizes neither implementation nor a commit.

### Dependencies

- A-10 complete and reviewed.
- Representative real-SSISDB fixtures include entry-point and non-entry-point packages, parameters, environment references, permission filtering, and supported/unsupported compatibility facts.
- A-11b may not begin until A-11a is reviewed and accepted. A-12 remains dependent on both children and parent A-11 acceptance.
- External fixture build/deployment/catalog configuration requires separate explicit review and authorization; missing fixture evidence blocks real acceptance.

### Explicit non-goals

- automatic or manual `Pipeline` registration;
- copying SSISDB beyond the approved current discovery observations, or treating PostgreSQL as native authority;
- historical native-version browsing/restoration/comparison or old ISPAC retrieval;
- SSIS execution;
- artifact retrieval or DTSX parsing; and
- graph projection.

### Conceptually affected areas

- SSISDB public catalog queries;
- native hierarchy and identity mappings;
- discovery candidate and provider-extension mappings;
- structured discovery errors;
- narrow provider-independent observation/staging contracts and refresh orchestration;
- PostgreSQL current-inventory generations, migration, atomic publication and access-context fencing;
- unit mapping, complete-scan, failure/security and PostgreSQL publication tests; and
- real-SSISDB discovery integration tests.

### RED verification

- Mapping tests fail for duplicate package names in different native paths, non-entry-point filtering, permission-filtered visibility, sensitive parameters, environment references, and unsupported compatibility facts.
- Discovery cannot distinguish executable candidates from non-entry-point project contents.
- The current buffered implementation truncates above its candidate/parameter/reference bounds and omits incompatible/non-entry-point inventory observations.
- No durable discovery generation can preserve a previous complete inventory after failure, fence obsolete publication or protect cached observations after access changes.

### GREEN verification

- Unit tests pass for native identity, independent format/entry-point eligibility, complete metadata association, project batching, bounded buffers/backpressure and safe error/cancellation behavior.
- Real-SSISDB tests return visible entry-point packages as executable candidates and do not present non-entry-point packages as directly executable.
- Results preserve provider instance, folder, project, package, and applicable version/format facts without making those fields universal core properties.
- Sensitive parameter or environment values remain masked and absent from logs and results.
- Discovery performs no provider mutation and creates no `Pipeline` record.
- Batch size 1 and larger sizes yield equivalent complete inventories, including empty scans, large projects and more data than the superseded total limits.
- Unknown-format definitions are persisted as observed/ineligible; `Partial` compatibility never conceals incomplete scan coverage.
- Project revision/metadata and final manifest checks detect observed changes without claiming an SSISDB snapshot or changing isolation settings. Unchanged LSN never suppresses fresh binding/reference reads.
- PostgreSQL tests prove atomic publication, previous-inventory preservation after interruption, concurrency/credential fencing and no stale disclosure after lost access. Not observed never means proven deletion.
- A-11a and A-11b real WSL/non-root SDK-container acceptance has zero failed/skipped tests; all six A-10 regression scenarios remain green.

### Exit criteria

Both child tasks are reviewed and accepted. Each invocation scans all visible projects and approved current metadata, then publishes a complete credential-scoped observational inventory atomically; compatible entry points retain stable provider-qualified references. Failed/interrupted refreshes preserve the previous complete inventory without authorizing stale disclosure. No registration, native history management or SSIS mutation is introduced.

## A-12 — Thin configuration/discovery API and UI

**Status:** Complete

Implementation and real live acceptance are complete; GitHub issue #13 remains open pending the final closure approval.

### Goal and scope

Define and implement only the Slice 1 REST contract and minimal authenticated UI needed to configure an external SSIS provider instance, validate it, and view executable discovery candidates. The approved refinements add exact trusted-operator/endpoint policies, durable asynchronous discovery admission and one hosted runner, plus generation-safe PostgreSQL inventory paging with targeted native visibility checks. Complete Milestone A end-to-end verification and user/contributor documentation; no SSISDB scan pagination or native execution is implied.

### Dependencies

- A-06 complete and reviewed.
- A-09 through A-11 complete and reviewed.
- Slice 1 API contract and authorization/error behavior reviewed before frontend implementation.
- Representative external SSISDB environment available for final acceptance.

### Explicit non-goals

- selecting or registering a candidate as a `Pipeline`;
- package execution or cancellation;
- `Run`/`RunAttempt` history;
- parameter-value entry for execution;
- GraphProjection or React Flow; and
- provider administration beyond the minimal configuration/validation/discovery journey.

### Conceptually affected areas

- provider-instance configuration and validation API;
- discovery API and sanitized error contract;
- minimal authenticated frontend views;
- frontend component tests;
- browser/end-to-end tests through Nginx; and
- Milestone A contributor/user documentation.

### RED verification

- An authenticated browser journey cannot configure, validate, or discover through the Nginx origin.
- Provider errors are flattened, unsafe, or not actionable.
- Discovery UI/API behavior automatically creates or implies a registered `Pipeline`.

### GREEN verification

- An authenticated user configures an external SSIS provider instance and validates it through the Nginx origin.
- The user sees structured executable package candidates from the representative SSISDB environment.
- Unreachable, authentication, permission, missing-catalog, unsupported-version, and no-result conditions are actionable and sanitized.
- Browser and API tests confirm that discovery remains distinct from registration.
- Clean-checkout development and production-style paths remain green with the provider feature present.

### Exit criteria

Milestone A meets its objective and applicable Definition of Done: the clean-checkout stack runs, OIDC works, a secure external SSIS provider instance can be configured and validated, and executable SSIS packages can be discovered without registration or execution.

## Hard scope boundary

Milestone A must not introduce:

- SSIS execution or cancellation;
- `Pipeline` registration;
- `Run` or `RunAttempt` concepts or behavior;
- workflow scheduling, external queues or native execution workers (the approved discovery-only hosted runner is described above);
- ISPAC/DTSX graph parsing or React Flow;
- deployment or authoring;
- Data Flow parsing or visualization;
- Kafka, message brokers, Redis, Kubernetes, or other unapproved infrastructure; or
- optional source-control, CI/CD product integration, metadata, lineage, external observability, or enterprise secret-manager dependencies.

If implementation pressure appears to require one of these items, stop the affected task and use the appropriate scope or architecture review rather than silently expanding Milestone A.

## Execution and review gate

- All tasks are initially `Planned`.
- A-01 is not `In Progress` merely because this plan exists.
- An explicit implementation instruction is required before A-01 begins.
- Each task must present its RED evidence, bounded diff, GREEN evidence, and exit-criteria review before the next dependent task begins.
- A-10, A-11, and A-12 cannot be declared complete using mocks alone; representative real-SSISDB evidence is required.

## Related documentation

- [FlowPlane MVP Implementation Plan](../mvp-implementation-plan.md)
- [FlowPlane MVP Domain Model](../../domain/mvp-domain-model.md)
- [FlowPlane SSIS Provider Technical Design](../../architecture/providers/ssis-provider-technical-design.md)
- [ADR 0001: Separate the Control Plane from the Execution Plane](../../adr/0001-control-plane-execution-plane-separation.md)
- [ADR 0002: Separate Integration Families](../../adr/0002-separate-integration-families.md)
- [ADR 0003: Keep FlowPlane Core Independent and Make External Integrations Additive](../../adr/0003-core-independence-and-progressive-integration.md)
- [ADR 0004: Use a Capability-Based Data-Pipeline Provider Architecture](../../adr/0004-capability-based-data-pipeline-provider-architecture.md)
- [ADR 0005: Define Execution Lifecycle, Authority, and Reconciliation](../../adr/0005-execution-lifecycle-authority-and-reconciliation.md)
