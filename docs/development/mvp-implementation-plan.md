# FlowPlane MVP Implementation Plan

## Status and authority

This is a public, contributor-facing implementation plan for the FlowPlane MVP. It translates the agreed [MVP scope](../product/mvp-scope.md), current [MVP domain model](../domain/mvp-domain-model.md), accepted ADRs, and [SSIS Provider Technical Design](../architecture/providers/ssis-provider-technical-design.md) into an incremental delivery sequence.

This document is not an ADR, application code, a database schema, or an API specification. Technical recommendations in this plan are starting hypotheses. They do not override accepted ADRs or settle choices that are explicitly assigned to later design checkpoints.

## 1. Implementation objectives

The primary MVP objective is an end-to-end user journey:

> A user can run a containerized FlowPlane installation, authenticate through Keycloak/OIDC, connect FlowPlane to an external SQL Server/SSISDB instance, discover an existing executable SSIS package, register it as a FlowPlane `Pipeline`, execute it, correlate the FlowPlane `Run` with the SSIS execution, observe its progress, result, and diagnostics, cancel it when appropriate, review execution history, inspect the package's read-only Control Flow visualization, and configure a basic recurring schedule.

The SSIS server and runtime remain external to FlowPlane containers. The first working end-to-end path is more important than implementing every future capability.

SSIS is the implementation proving ground, not the FlowPlane domain model. When implementation exposes pressure on a provider-independent concept:

1. record the concrete problem;
2. determine whether it is SSIS-specific;
3. compare it with the cross-platform [domain research](../domain/data-pipeline-concepts.md); and
4. change shared architecture only when broader provider semantics justify the change.

The MVP team must not design hypothetical future providers while implementing SSIS.

## 2. Delivery principles

- Deliver vertical slices rather than building every infrastructure layer first.
- Produce working end-to-end behavior as early as possible.
- Use PostgreSQL as the authoritative durable store for FlowPlane-owned orchestration state.
- Do not require Kafka for the MVP.
- Keep the SSIS runtime external to FlowPlane.
- Treat FlowPlane-owned processes and containers as restartable and disposable.
- Recover incomplete execution work through persisted state, native correlation, and reconciliation.
- Keep SSIS-specific code and semantics behind the provider boundary.
- Introduce generic abstractions only when current requirements justify them.
- Test provider correctness against a real SQL Server and SSISDB environment; mocks alone are insufficient.
- Keep optional source-control, CI/CD, metadata, lineage, enterprise-secret, and external-observability integrations out of the MVP runtime dependency graph.
- Treat `GraphProjection` as a presentation projection, not the canonical `Pipeline` model.
- Do not let Data Flow visualization delay the required Control Flow MVP.
- Define contracts and persistence only as each slice requires them; do not design the full future platform up front.

## 3. Technology baseline

The baseline below is a planning recommendation as of October 2026. Repository manifests and container configuration must pin exact supported patches or immutable image references during implementation. Floating `latest` tags are not acceptable for a reproducible development environment.

| Area | Major-version recommendation | Rationale and pinning policy |
| --- | --- | --- |
| Backend runtime | [.NET 10 LTS](https://dotnet.microsoft.com/en-us/platform/support/policy) with ASP.NET Core and C# | .NET 10 is active LTS through November 14, 2028. .NET 8 and .NET 9 reach end of support on November 10, 2026, so beginning a greenfield project on either would create immediate migration pressure. Pin the SDK/runtime patch in repository and container files. |
| SQL Server client | `Microsoft.Data.SqlClient` | Use the supported modern .NET SQL Server provider. Select and pin the exact package version during Milestone 0 compatibility testing. |
| FlowPlane database | [PostgreSQL 18](https://www.postgresql.org/support/versioning/) | PostgreSQL 18 is supported through November 14, 2030 and is the recommended development/reference major. Pin a supported minor/image digest and document any compatibility range separately. |
| Frontend | [React 19](https://react.dev/versions) with TypeScript | React 19 is the current stable major. Use strict TypeScript and pin exact packages in the frontend manifest and lockfile. |
| Type system | [TypeScript 6](https://www.typescriptlang.org/docs/handbook/release-notes/typescript-6-0.html) | Current stable starting point for a new project; exact compiler patch and explicit compiler options belong in repository configuration. |
| Graph presentation | `@xyflow/react` / React Flow | Required for the read-only Control Flow view. Pin the compatible release only when the frontend is scaffolded; it must not define backend domain storage. |
| Frontend build | [Vite 8](https://vite.dev/releases) as the starting recommendation | Vite is a current, supported, React-compatible build tool. Confirm Node/runtime compatibility and pin a supported release during the frontend gate. This is not a permanent architectural commitment. |
| Identity | [Keycloak 26](https://www.keycloak.org/docs/) through standard OIDC/OAuth2 | Keycloak is the intended MVP identity provider. Pin one tested stable image, realm bootstrap approach, and upgrade procedure during Milestone 0; do not couple application logic to Keycloak-specific APIs. |
| Development environment | Linux/WSL2, Docker, and Docker Compose | Provide one documented containerized path for contributors while keeping the external SSIS environment outside the Compose stack. |

No frontend component library is selected merely for completeness. Kafka, Redis, RabbitMQ, Temporal, Kubernetes, and similar infrastructure require a demonstrated MVP need and a separate review before introduction.

## 4. Initial implementation topology

**Technical recommendation:** Start the MVP with one backend deployable that hosts API, scheduling, reconciliation, and background responsibilities behind internal boundaries.

```text
FlowPlane Web
      |
      v
FlowPlane Backend
      |
      +---- PostgreSQL
      |
      +---- Keycloak / OIDC
      |
      +---- SSIS Provider
                  |
                  v
           external SSISDB
                  |
                  v
           external SSIS runtime
```

For development:

```text
Docker Compose
|
+-- FlowPlane Web
+-- FlowPlane Backend
+-- PostgreSQL
+-- Keycloak

External
|
+-- SQL Server / SSISDB / SSIS
```

This is the simplest starting topology, not a permanent modular-monolith, microservice, or process-topology decision. Scheduling, reconciliation, and provider work must have internal boundaries that allow later process separation. Correctness must never depend on the backend process remaining alive: durable FlowPlane state lives in PostgreSQL, provider-native state remains in SSISDB, and recovery uses reconciliation.

## 5. Proposed repository structure

The first implementation should favor a small number of focused projects over a project for every concept.

```text
src/
  backend/
    FlowPlane.Api/
    FlowPlane.Domain/
    FlowPlane.Application/
    FlowPlane.Infrastructure/
    FlowPlane.Providers.Ssis/
  frontend/

tests/
  unit/
  integration/
  e2e/
  fixtures/

deploy/
  compose/

docs/
```

**Technical recommendation:** Begin with five backend boundaries:

- `FlowPlane.Domain` — provider-independent domain behavior and accepted lifecycle concepts;
- `FlowPlane.Application` — use cases, orchestration, and the smallest provider/capability contracts needed by current slices;
- `FlowPlane.Infrastructure` — PostgreSQL persistence, migrations, identity adapters, and other replaceable infrastructure;
- `FlowPlane.Providers.Ssis` — SSISDB access, native mappings, reconciliation support, and artifact parsing; and
- `FlowPlane.Api` — composition root, HTTP host, OIDC integration, and initially hosted background responsibilities.

This is an implementation-gate recommendation, not a final project list. Merge projects if boundaries remain clear; split only when coupling or independent testing/deployment pressure justifies it. In particular, `FlowPlane Core` remains a logical architecture boundary, not a required project name.

The frontend should organize shared product views separately from capability/provider-aware views. SSIS extensions belong behind provider/capability components rather than widespread `provider == ssis` conditionals.

## 6. Containerized development baseline

A contributor using WSL2 Ubuntu should eventually be able to run a small documented command set and obtain:

- the FlowPlane frontend;
- the FlowPlane backend;
- PostgreSQL with migrations/bootstrap applied;
- Keycloak with a development realm/client bootstrap;
- health and dependency-readiness results.

SSIS and SQL Server do not belong in the required FlowPlane Compose stack. Development configuration must instead accept an externally reachable SQL Server/SSISDB endpoint.

The development baseline must:

- use pinned images and dependency versions;
- avoid committed real credentials;
- support environment variables, development-secret references, example configuration, and ignored local overrides;
- distinguish process liveness from dependency readiness;
- wait or retry safely for PostgreSQL and Keycloak readiness; and
- preserve PostgreSQL data across ordinary FlowPlane container restarts.

No Compose file is defined by this document.

## 7. Development CI

Repository-development CI is distinct from FlowPlane's optional product-level CI/CD Integration family. GitHub Actions may build and test this repository without becoming a runtime dependency or product integration.

An initial repository pipeline should eventually cover:

- backend restore and build;
- backend unit tests;
- frontend dependency installation from a lockfile, type checking, build, and tests;
- formatting and lint checks;
- container build verification;
- migration/bootstrap verification against PostgreSQL; and
- Markdown and relative/external link checks where practical.

Real SSISDB integration tests require a separately configured environment and credentials. They should be runnable and reported explicitly, but must not prevent ordinary contributors from running unit and artifact tests when no SSIS environment is available. Protected CI environments may run the SSIS suite on demand or on a controlled schedule. No workflow files are created by this plan.

## 8. Milestone 0 — Repository and development foundation

### Goal

Create only the foundation required to deliver Slice 1 without constructing a general framework platform.

### Dependencies

- Accepted ADRs and current MVP/provider technical designs.
- Resolution of the early implementation gates in section 31.

### Task list

1. Scaffold the minimal backend solution/projects and frontend application.
2. Establish dependency-direction checks or conventions that keep SSIS out of the provider-independent domain.
3. Add minimal multi-stage Dockerfiles and a development Compose skeleton for web, backend, PostgreSQL, and Keycloak.
4. Establish environment-specific configuration, ignored local overrides, and development-secret handling.
5. Add PostgreSQL connectivity and the selected migration mechanism with an empty/baseline migration path.
6. Configure minimal Keycloak realm/client bootstrap and standards-based OIDC validation.
7. Add backend liveness/readiness endpoints and structured application logging.
8. Add baseline backend/frontend unit-test runners and clean-checkout development documentation.
9. Establish the initial repository CI checks described in section 7, without SSIS becoming a boot prerequisite.

### Conceptually affected areas

- `src/backend/*`
- `src/frontend/*`
- `tests/unit/*` and baseline integration-test infrastructure
- `deploy/compose/*`
- repository dependency manifests, lockfiles, and contributor documentation

### RED/GREEN verification

- **RED:** A clean checkout has no reproducible build, test, OIDC, health, or container startup path.
- **GREEN:** The documented clean-checkout commands restore/build/test both applications, start the four Compose services, apply/bootstrap PostgreSQL, validate minimal OIDC configuration, and return healthy backend status.
- Restart the FlowPlane web/backend containers and confirm PostgreSQL state remains.
- Start the stack without SSIS configuration and confirm FlowPlane still boots.

### Exit criteria

- The repository builds from a clean checkout and tests execute.
- Frontend and backend start successfully.
- PostgreSQL and Keycloak start through Compose with pinned images.
- Minimal OIDC authentication/validation works.
- Backend health reporting works.
- Restarting FlowPlane containers does not destroy PostgreSQL state.
- SSIS is not required merely to boot FlowPlane.

Stop foundation work at this point and begin the provider proving path.

## 9. Slice 1 — SSIS connectivity and discovery

### Goal

Prove that the Linux-containerized backend can securely connect to an external SSISDB and discover executable native definitions.

### Dependencies

- Milestone 0 development stack, configuration, health, and test foundations.
- Security checkpoint for the first real provider credential.
- Access to a supported real SQL Server/SSISDB test environment.

### Task list

1. Establish the SSIS provider module and only the provider contracts required for validation and discovery.
2. Implement the working `ProviderType`/`ProviderInstance` configuration model without defining future plugin loading.
3. Add `SecretReference` resolution sufficient for provider credentials, encrypted SqlClient options, and redacted failures.
4. Validate SQL Server reachability, authentication, SSISDB availability, supported catalog surface/version, and effective baseline permissions separately.
5. Query visible folders, projects, packages, package entry-point status, format/version facts, and stable native hierarchy.
6. Add parameter metadata and environment-reference discovery only to the depth needed to inform registration/executability.
7. Map results to provider-qualified `NativeDefinitionReference` candidates and structured provider-specific errors.
8. Expose one thin slice API/UI for configuring an instance and viewing candidates; define only that slice's API contract first.

### Conceptually affected areas

- application provider contracts and discovery use case;
- SSIS provider SqlClient/catalog access and mappings;
- provider configuration/secret infrastructure;
- thin API and provider-instance/discovery frontend views;
- SSIS integration-test harness and fixtures.

### RED/GREEN verification

- Unit tests cover native hierarchy/reference mapping, entry-point filtering, and safe error classification.
- Real SSISDB tests cover valid and invalid credentials, unreachable server, missing SSISDB, insufficient permission, supported/unsupported compatibility facts, and entry-point/non-entry-point packages.
- Security tests confirm credentials and connection strings do not appear in logs or returned errors.

### Exit criteria

A user or contributor can configure an external SSIS `ProviderInstance` and retrieve a structured list of executable package candidates. Discovery does not create `Pipeline` records automatically.

## 10. Slice 2 — Pipeline registration

### Goal

Register a selected discovered package as a stable provider-independent FlowPlane `Pipeline`.

### Dependencies

- Slice 1 discovery and native-reference re-resolution.
- The MVP PostgreSQL schema technical-design checkpoint in section 24.
- A slice-specific registration/read API contract.

### Task list

1. Implement the minimum persistence for `ProviderInstance`, `Pipeline`, `NativeDefinitionReference`, and audit/reference information.
2. Add the registration use case with duplicate/conflict behavior defined in the slice contract.
3. Re-resolve a stored native reference against SSISDB before presenting it as currently available/executable.
4. Represent stale, missing, changed, and non-entry-point native targets without silently relinking them.
5. Record the registering actor and significant configuration action.
6. Add the minimal API and frontend flow for selecting a discovery result, registering it, and viewing the registered `Pipeline`.

### Conceptually affected areas

- domain/application `Pipeline` behavior;
- PostgreSQL persistence and migration;
- provider reference resolution;
- registration/read API contract;
- provider/package selection and registered-pipeline frontend views.

### RED/GREEN verification

- Domain/application tests prove discovery alone does not register and registration preserves a provider-independent identity.
- PostgreSQL integration tests prove persistence and clean reloading.
- SSIS integration tests prove re-resolution, missing target, non-entry-point target, and native project/package change handling.
- A frontend flow test registers a selected candidate without exposing SSISDB-shaped core resources.

### Exit criteria

A selected SSIS package can be registered as a FlowPlane `Pipeline` and later resolved to the correct external package. No generic `PipelineVersion` is introduced.

## 11. Slice 3 — First manual execution

### Goal

Prove the core promise: FlowPlane can initiate an SSIS execution and durably correlate it.

### Dependencies

- Slice 2 registered `Pipeline` and provider resolution.
- Durable MVP schema for `Run`, `RunAttempt`, `ExecutionContext`, `NativeRunReference`, actor/audit, and ambiguity metadata.
- Slice-specific manual-run API contract.
- A simple entry-point package requiring minimal configuration.

### Task list

1. Implement only the accepted provider-independent `Run` and `RunAttempt` lifecycle needed for manual dispatch.
2. Capture a non-secret `ExecutionContext` snapshot and actor reference.
3. Persist `Run` and `RunAttempt.Created`, then transition durably to `Dispatching` before the remote creation boundary.
4. Call `catalog.create_execution` through the SSIS provider.
5. Persist the returned SSIS execution ID as `NativeRunReference` before further provider operations and mark the attempt `Submitted`.
6. Apply the minimal required pre-start configuration, then call `catalog.start_execution`.
7. Preserve ambiguous dispatch when the create response is lost; do not claim exactly-once execution or retry native creation blindly.
8. Add the minimal Run action/result UI showing FlowPlane and native correlation identity.

### Conceptually affected areas

- domain/application execution use case and lifecycle behavior;
- PostgreSQL execution persistence/migration;
- SSIS create/configure/start implementation;
- manual-run API and UI;
- provider and backend integration tests.

### RED/GREEN verification

- Lifecycle tests cover Created, Dispatching, Submitted, invalid transitions, and immutable terminal behavior already required by ADR-0005.
- PostgreSQL tests prove durable state on both sides of provider calls; no transaction remains open across SQL Server operations.
- Real SSISDB tests create and start one simple package and preserve its execution ID.
- A lost-response simulation proves the attempt remains explicitly ambiguous rather than auto-creating another execution.

### Exit criteria

A user can select Run and see a durable FlowPlane `Run`/`RunAttempt` correlated with the SSIS execution ID. Observation and final outcome arrive in Slice 4.

## 12. Slice 4 — Observation and execution history

### Goal

Observe the native execution through SSISDB, reconcile it with FlowPlane state, and survive backend restart.

### Dependencies

- Slice 3 durable native correlation.
- Observation/history API contract and lifecycle mapping design bounded to accepted ADR-0005 states.

### Task list

1. Implement provider observation through public `catalog.executions`/`catalog.operations` behavior.
2. Preserve native status, result, timestamps, and identity separately from normalized FlowPlane lifecycle state.
3. Implement accepted `RunAttempt` transitions, `Run` phase/outcome derivation, and observation freshness/unavailability.
4. Add bounded polling/background observation without making process memory authoritative.
5. Recover incomplete known-native-reference attempts on startup/restart.
6. Expose bounded execution history, current/last-known status, and basic provider diagnostic messages.
7. Add the minimal execution-history and run-detail frontend experience.

### Conceptually affected areas

- execution lifecycle/reconciliation application logic;
- SSIS provider observation and diagnostics;
- PostgreSQL observation/history persistence;
- hosted background responsibilities;
- history/detail API and frontend views.

### RED/GREEN verification

- Cover native success, failure, a short execution observed directly from Submitted to Terminal, terminal history, and provider unavailability without false failure.
- Start a long-running package, restart the backend, reconnect through the persisted native reference, and verify the eventual native and FlowPlane outcomes.
- Verify bounded message queries and redaction.

### Exit criteria

FlowPlane can start a package, restart while it runs, reconnect, and display the correct eventual result and history. This is the first major MVP proof.

## 13. Slice 5 — Cancellation

### Goal

Request native cancellation durably and report the provider result truthfully.

### Dependencies

- Slice 4 observation and reconciliation path.
- Slice-specific cancellation command contract and authorization rule.

### Task list

1. Persist cancellation intent, request time, actor, and audit information before the provider call.
2. Call `catalog.stop_operation` when a correlated native execution is cancellable and the principal is authorized.
3. Continue native observation and reconciliation until the outcome is known.
4. Handle already-terminal executions, invalid operation identity, permission failure, repeated requests, and cancellation/success or cancellation/failure races.
5. Expose cancellation availability and intent/result separately in API/UI behavior.

### Conceptually affected areas

- execution application/domain behavior;
- cancellation/reconciliation persistence;
- SSIS provider cancellation;
- run-detail API/UI and audit;
- real SSIS integration fixtures for long-running execution.

### RED/GREEN verification

- Lifecycle tests prove a request is not immediate proof of a `Cancelled` outcome.
- Real SSIS tests cover successful stop, already terminal, repeated stop, denied permission, and native completion racing the request.
- Audit tests preserve both the actor's intent and final provider fact.

### Exit criteria

A long-running SSIS package can be cancelled from FlowPlane, and FlowPlane preserves both cancellation intent and the final native result.

## 14. Slice 6 — Reconciliation and ambiguous recovery

### Goal

Complete ADR-0005 recovery behavior for incomplete, stale, or ambiguous attempts.

### Dependencies

- Slices 3–5 lifecycle, observation, and cancellation behavior.
- Durable ownership/claiming design for reconciliation work inside the initial backend topology.

### Task list

1. Identify incomplete attempts from PostgreSQL and schedule idempotent reconciliation work.
2. Recover known Submitted executions, including a crash before start and a lost `start_execution` response.
3. Preserve last-known facts during provider unavailability and refresh stale observations after recovery.
4. Continue reconciliation after a terminal FlowPlane `Run` when native execution remains active, ambiguous, or needs follow-up.
5. Detect project redeployment/version mismatch and avoid silently running a changed definition under prior intent.
6. Represent unresolved ambiguous `create_execution` response loss as action-required; never use heuristic automatic native recreation.
7. Exercise concurrent/repeated reconciliation and backend restart behavior.

### Conceptually affected areas

- reconciliation application service/background worker boundary;
- PostgreSQL claim/progress metadata;
- SSIS recovery queries and native evidence;
- operational status/API/UI for ambiguity and stale observations.

### RED/GREEN verification

- Failure-injection tests cover each remote boundary and restart point.
- Tests prove repeated reconciliation does not create another logical Run or blindly create another native execution.
- A terminal TimedOut Run with a still-running native attempt remains observable/reconcilable without reopening the Run.

### Exit criteria

Incomplete Runs/Attempts survive process/container restart and are either safely reconciled or surfaced as explicitly ambiguous/action-required.

## 15. Slice 7 — Parameters and SSIS environments

### Goal

Execute representative parameterized and environment-aware SSIS packages securely.

### Dependencies

- Slice 6 recovery path.
- Security checkpoint for sensitive execution values.
- Parameter/environment execution contract bounded to SSIS provider semantics.

### Task list

1. Discover project/package parameter metadata, required/sensitive flags, server/design metadata, and applicable environment references.
2. Allow selection of an SSISDB environment reference before `catalog.create_execution`.
3. Accept permitted execution values and logging-level option without persisting raw secrets in `ExecutionContext`.
4. Apply execution values through `catalog.set_execution_parameter_value` before start.
5. Capture the non-secret effective intent/configuration references in `ExecutionContext`.
6. Use `catalog.execution_parameter_values` as recovery evidence while recognizing that masked sensitive values cannot prove their original content.
7. Surface missing required values, invalid references, masking, and unsafe resume conditions clearly.

### Conceptually affected areas

- SSIS parameter/environment discovery and execution;
- execution application logic and `ExecutionContext` persistence;
- controlled sensitive-input/secret handling;
- run form/API/UI and recovery behavior;
- parameterized SSIS fixtures.

### RED/GREEN verification

- Cover required, optional/default, environment-referenced, sensitive, missing, and type-invalid parameter cases.
- Cover an invalid environment reference.
- Restart after native creation but before start; inspect native parameter state and refuse unsafe automatic continuation when intended sensitive configuration cannot be proved.
- Confirm sensitive values are masked in storage, logs, diagnostics, and UI.

### Exit criteria

Representative parameterized SSIS packages execute securely through FlowPlane with truthful recovery behavior.

## 16. Slice 8 — ISPAC retrieval and DTSX2 structural parsing

### Goal

Produce a provider-internal `SsisControlFlowModel` from a deployed package without Windows SSIS runtime libraries.

### Dependencies

- Registered Pipeline/native-reference resolution.
- SSIS project `READ` permission and `catalog.get_project` support.
- Artifact security/resource-limit design.

### Task list

1. Retrieve the current project ISPAC through `catalog.get_project` only when artifact inspection is requested.
2. Add bounded in-memory OPC/ZIP reading with traversal, decompression, malformed-archive, and resource-limit defenses.
3. Resolve the manifest and selected package part without persistent extraction.
4. Add hardened XML parsing and DTSX/DTSX2 format/version detection.
5. Parse package root, nested executable hierarchy, `Task`/`Container`/`Unrecognized` structural roles, and native types/refIds.
6. Parse Sequence, For Loop, and Foreach Loop details plus precedence constraints.
7. Preserve safely understood custom/third-party executable roles and raw native type information.
8. Parse supported `DTS:DesignTimeProperties` layout metadata separately from semantics.
9. Return explicit complete, partial, protected/unsupported, and unavailable outcomes.

### Conceptually affected areas

- SSIS artifact retrieval;
- provider-internal ISPAC and DTSX parsers;
- artifact fixtures and hostile-input tests;
- provider capability result/error representation.

### RED/GREEN verification

- Artifact tests cover basic, nested, Sequence, For Loop, Foreach Loop, Data Flow Task, custom/unknown, protected, malformed, oversized/hostile, and multiple-format fixtures.
- Real SSISDB integration retrieves an ISPAC and selects the registered package.
- Tests confirm parser code does not load or execute `Microsoft.SqlServer.Dts.Runtime` or task/component implementations.

### Exit criteria

Given a registered SSIS `Pipeline`, FlowPlane can produce a provider-internal `SsisControlFlowModel` from the deployed package without `Microsoft.SqlServer.Dts.Runtime`.

## 17. Slice 9 — `GraphProjection` and read-only Control Flow UI

### Goal

Project the provider-internal model into a read-only React Flow monitoring/exploration view.

### Dependencies

- Slice 8 parser outcomes and static model.
- Slice-specific `GraphProjection` contract designed before frontend implementation.

### Task list

1. Define only the projection fields required for structural role, hierarchy, native reference/type, layout, precedence display, and warnings.
2. Map `SsisControlFlowModel` to provider-independent presentation data with namespaced SSIS details.
3. Render tasks, nested/grouped containers, and precedence edges in React Flow.
4. Use valid native layout where supported and deterministic auto-layout otherwise.
5. Add a task/container details panel and explicit partial/unavailable parsing messages.
6. Enforce read-only interactions; no editing, serialization back to DTSX, or persisted generic Node/Edge model.

### Conceptually affected areas

- application/provider graph projection boundary;
- graph API contract;
- capability-aware frontend graph, layout, and detail components;
- parser/projection/frontend tests.

### RED/GREEN verification

- Projection tests cover hierarchy, native extension preservation, constraints, warnings, and deterministic fallback layout.
- Frontend tests cover grouped rendering, native layout, fallback layout, unavailable capability, partial parse, and absence of editing actions.
- An end-to-end demonstration opens a registered package without SSDT.

### Exit criteria

A user can inspect a registered package's read-only Control Flow in FlowPlane. React Flow state is not the canonical backend model.

## 18. Slice 10 — Runtime Control Flow overlay and loop UX

### Goal

Overlay useful current/historical SSIS execution facts on the static Control Flow projection.

### Dependencies

- Slice 4 observations/diagnostics.
- Slice 9 static projection and native executable references.
- Validated static-to-runtime correlation strategy for supported SSIS versions.

### Task list

1. Correlate DTSX executable identities/paths with `catalog.executables`, `catalog.executable_statistics`, and relevant messages.
2. Overlay executable result/status, start/end, duration, warnings, and errors without replacing provider-native facts.
3. Add iteration-aware history and timing from `catalog.executable_statistics`.
4. Show For Loop expressions/references and current/completed iteration; show an expected total only when reliably derivable, otherwise “Total unknown.”
5. Show Foreach enumerator type/configuration, variable mappings, and observed iteration/value context when available.
6. Distinguish observed value, unavailable/unobserved value, and observed null; never promise debugger-style live variables.
7. Disclose version/correlation mismatch when the current graph may not represent the historical execution.

### Conceptually affected areas

- SSIS runtime diagnostic/correlation logic;
- runtime `GraphProjection` overlay;
- capability-aware graph/detail frontend;
- loop and historical-run fixtures/tests.

### RED/GREEN verification

- Provider tests cover executable correlation, per-iteration rows, duplicate display names, nested paths, missing telemetry, and project-version mismatch.
- Frontend tests cover active/terminal overlays, warnings/errors, “Total unknown,” and the three value-availability states.
- Demonstrate one running and one historical package with nested/loop monitoring.

### Exit criteria

A running or historical package displays useful task/container state over its Control Flow graph, including truthful loop monitoring.

## 19. Slice 11 — Basic recurring scheduling

### Goal

Create durable recurring Runs through the already-proven execution path.

### Dependencies

- Slices 3–7 manual execution, observation, recovery, and parameter behavior.
- Schedule persistence design and slice-specific API contract.
- Explicit timezone and occurrence-identity rules.

### Task list

1. Persist a FlowPlane-owned recurring schedule with timezone, enabled state, and next-occurrence information.
2. Calculate due occurrences with explicit timezone/daylight-saving tests.
3. Assign each occurrence a stable logical identity and enforce at-most-one FlowPlane `Run` per occurrence through PostgreSQL-backed correctness.
4. Create scheduled Runs through the same application path as manual Runs, with scheduled trigger information in `ExecutionContext`.
5. Recover schedule evaluation after backend restart without relying on in-memory timers as authority.
6. Add basic schedule create/update/enable/disable and next-occurrence API/UI behavior.

### Conceptually affected areas

- `Schedule` domain/application behavior;
- PostgreSQL schedule/occurrence persistence;
- initially hosted scheduler responsibility;
- scheduling API/frontend;
- time and concurrency tests.

### RED/GREEN verification

- Cover timezone and daylight-saving boundaries, disabled schedules, restart, concurrent due-occurrence processing, and duplicate prevention.
- Demonstrate that manual and scheduled invocations use the same `Run`/`RunAttempt` path.
- Restart the backend around a due time and verify one logical Run, not zero or two.

### Exit criteria

A recurring schedule runs the same `Pipeline` through the same durable execution path, survives backend restart without duplicating an occurrence, and can be disabled.

Do not add distributed scheduler HA, Kafka, leader election, event triggers, complex misfire policy, distributed queues, or DAG orchestration.

## 20. MVP acceptance milestone

The MVP is accepted when the following end-to-end scenario passes against a representative external SSIS environment and remains aligned with the [MVP scope](../product/mvp-scope.md):

1. Start Minimal FlowPlane through the documented containerized development setup.
2. Authenticate with Keycloak through OIDC.
3. Configure an external SSIS `ProviderInstance`.
4. Discover an executable SSIS package.
5. Register it as a FlowPlane `Pipeline`.
6. Manually execute it.
7. Observe FlowPlane `Run`/`RunAttempt` state and native SSIS status.
8. View relevant diagnostics.
9. Restart the FlowPlane backend during an active execution and recover state.
10. Cancel a long-running package.
11. Execute a parameterized, environment-aware package.
12. View execution history.
13. Retrieve and display its read-only SSIS Control Flow.
14. Inspect nested and loop containers.
15. Configure a recurring schedule.
16. Demonstrate at least two scheduled executions.
17. Disable the schedule and verify that no subsequent occurrence is created, then re-enable it and verify that the next-execution indication is restored.

This milestone validates the integrated product. Passing isolated provider, backend, or frontend tests does not replace the end-to-end acceptance journey.

## 21. Deferred and stretch work

The following work remains outside the required MVP and must not block its completion:

- static Data Flow visualization, unless implementation proves inexpensive enough to include safely without delaying the Control Flow MVP;
- rich Data Flow runtime visualization;
- component-path row-count animation;
- ISPAC deployment;
- incremental DTSX deployment;
- SSIS validation UX;
- SSIS authoring or designer capabilities;
- `PipelineVersion`;
- Git integration;
- product-level CI/CD integrations;
- OpenMetadata;
- OpenLineage;
- Kafka;
- external observability integration;
- external enterprise secret-manager integration;
- multi-tenancy;
- Kubernetes production architecture;
- other Data-Pipeline Providers.

Deferred work may be researched in a bounded spike where this plan explicitly allows one, but it does not become part of MVP acceptance without a separate scope decision.

## 22. Optional Data Flow exploratory spike

After Control Flow parsing is proven in Slice 8, an optional bounded research spike may test whether the DTSX2 parser architecture can recognize:

- Pipeline Task;
- components;
- sources, transformations, and destinations;
- inputs and outputs;
- paths.

The spike may add focused fixtures and tests. It must not turn a Data Flow UI into an MVP requirement. It should also investigate the availability and overhead of `catalog.execution_component_phases` and `catalog.execution_data_statistics`, then record evidence for post-MVP planning. It must not delay Slices 9–11 or the MVP acceptance milestone.

## 23. Deployment and designer exploratory boundary

SSIS deployment and designer work remain future capabilities. MVP implementation must not build them, but it should avoid choices that make these provider-native flows impossible:

```text
ISPAC
  -> catalog.deploy_project

DTSX
  -> catalog.deploy_packages

Future designer
  -> provider-internal authoring model
  -> DTSX
  -> deployment
```

No generic FlowPlane `Deployment` entity is required for the MVP. Preserving this boundary does not authorize deployment endpoints, authoring models, or designer UI during MVP work.

## 24. Database-design checkpoint

Schedule a dedicated PostgreSQL logical-schema technical-design task after the accepted MVP domain and SSIS provider designs are stable and before Slices 2–4 require durable persistence. The design should cover only the MVP concepts needed at that point, including:

- `ProviderInstance`;
- `Pipeline`;
- `Schedule`;
- `Run`;
- `RunAttempt`;
- `ExecutionContext`;
- `NativeRunReference`;
- provider observations;
- cancellation and reconciliation information;
- audit information;
- secret references.

The checkpoint must define ownership, durability, concurrency, migration, retention, and recovery needs before implementation begins. This plan intentionally does not define tables, columns, keys, indexes, ORM mappings, or migration files.

## 25. API-design checkpoint

Before frontend work begins for each vertical slice, define and review only the REST contract required by that slice. API contracts must follow FlowPlane domain and capability semantics rather than expose SSISDB table shapes or React Flow serialization.

Each checkpoint should cover the slice's user journey, authorization needs, errors, unavailable capabilities, concurrency/idempotency expectations where relevant, and compatibility implications. It must not pre-design the complete future FlowPlane API or turn preliminary payload sketches into permanent contracts without review.

## 26. Security checkpoint

Before merging any handling of real provider credentials or sensitive parameters:

- review the `SecretReference` implementation and its standalone development behavior;
- verify that plaintext provider credentials are not persisted or logged;
- validate SQL encryption and certificate configuration;
- review the minimum SSISDB permissions required by the implemented slice;
- test sensitive-parameter masking;
- test exception, diagnostic, and structured-log redaction.

OIDC authentication alone does not complete this checkpoint. Authorization, audit attribution, provider permissions, transport protection, and secret handling must be tested at their applicable boundaries. The checkpoint does not select a production secret backend or finalize RBAC architecture.

## 27. Testing layers

### Unit tests

Cover provider-independent domain behavior, accepted `Run`/`RunAttempt` transitions, parsing, mappings, validation helpers, and deterministic scheduling or projection behavior. Unit tests must remain fast and require neither SSIS nor containerized dependencies.

### Provider artifact tests

Use bounded ISPAC and DTSX fixtures, including malformed or hostile artifacts, layout variants, nested containers, and custom or unrecognized components. Fixtures must not contain private production packages or credentials.

### SSIS integration tests

Run against a real SQL Server and SSISDB environment to verify permissions, discovery, execution, cancellation, recovery, parameters, and observation. These tests may be opt-in for ordinary contributors, but the provider slices cannot be declared complete using mocks alone.

### Backend integration tests

Exercise PostgreSQL persistence, migrations, concurrency behavior, restart/recovery behavior, authentication boundaries where practical, and application-to-provider orchestration using controlled test doubles where the test is not specifically proving SSIS behavior.

### Frontend tests

Cover critical user flows, graph rendering, read-only behavior, provider/capability-unavailable states, loading and error states, and the distinction between observed, unavailable, and null values.

### End-to-end tests

Automate selected critical journeys across browser, backend, PostgreSQL, and Keycloak. Keep the full external-SSIS acceptance journey available as a separately configured test because SSIS remains external to the standard Compose stack.

Ordinary unit-test workflows must not require access to a live SSIS environment.

## 28. Definition of Done per slice

Where applicable, a completed slice includes:

- the smallest code change that delivers its stated end-to-end behavior;
- relevant unit, integration, provider, frontend, and end-to-end tests;
- a reviewed migration when persistent state changes;
- concise contributor or user documentation;
- explicit error handling and actionable diagnostics;
- security review proportional to the data and operations introduced;
- compatibility with the containerized development environment;
- evidence that provider-specific behavior remains behind its boundary;
- a clean build and applicable test run from a fresh checkout;
- an observable end-to-end demonstration against representative dependencies.

A slice is not complete merely because backend code compiles. Any deliberately deferred test, operational issue, or design question must be recorded and must not invalidate the slice's exit criteria.

## 29. Implementation task granularity

Break each slice into reviewable tasks that modify a bounded area and have a clear failing-to-passing verification path. Prefer tasks that can be completed in one focused Superpowers/Codex implementation session and avoid simultaneous backend, frontend, database, and provider rewrites.

Every slice in this plan therefore states:

- its task list;
- dependencies and prerequisite checkpoints;
- the files or modules affected conceptually;
- RED/GREEN verification;
- exit criteria.

During execution, refine a slice into only as many tasks as are needed to preserve independent review and verification. Do not create hundreds of speculative microtasks before implementation evidence exists.

## 30. First coding milestone recommendation

### Milestone A — Development foundation and SSIS discovery

**Goal:** From a clean checkout, run the FlowPlane development stack, authenticate, configure an external SSIS `ProviderInstance`, and discover executable SSIS packages.

This milestone comprises Milestone 0 and Slice 1. Implement it as a short sequence of bounded changes: establish the minimum build/test skeleton, bring up PostgreSQL and Keycloak, prove OIDC and health, then add SSIS connectivity and discovery behind the provider boundary.

The first coding commit must contain only the minimum development foundation needed for subsequent work. It must not include execution, `Run`/`RunAttempt`, package registration, graph visualization, or scheduling. Later commits in Milestone A may deliver discovery, but execution remains outside the milestone.

**Exit:** The stack starts from a clean checkout, authenticates through Keycloak/OIDC, accepts development configuration for an external SSIS provider instance, connects securely to its SSISDB, and returns structured executable package candidates without registering them automatically.

### Milestone B — First durable manual execution

**Goal:** Register one discovered package and execute it through the accepted `Run`/`RunAttempt` lifecycle.

This milestone comprises Slices 2–4 and the database/API checkpoints they require. Keep the first package intentionally simple. Persist FlowPlane intent around the remote boundaries, retain its native SSIS execution identifier, observe it through the accepted lifecycle, and recover a known correlated execution after backend restart. Full ambiguous-dispatch recovery, parameters, visualization, and scheduling follow in their dedicated slices.

**Exit:** A user can register one discovered package, manually create a durable FlowPlane `Run`, initiate and observe one SSIS execution through its `RunAttempt`, see the correlated SSIS execution identifier and eventual result, and recover observation after backend restart without an exactly-once claim.

## 31. Implementation gates

Resolve these choices explicitly before the affected coding starts. Defaults are recommendations for planning, not hidden architecture decisions; exact versions and mechanics must be recorded in the implementing change.

| Gate | Pragmatic default | Why | Changeability / significance |
| --- | --- | --- | --- |
| Backend project/module layout | Begin with the small separation proposed in section 5, combining projects if a boundary has no immediate dependency-enforcement value. | It protects the provider-independent core without front-loading framework structure. | Easy to adjust early; architectural review is required if it changes provider boundaries or deployable/process topology. |
| Frontend scaffolding and build tool | React 19, TypeScript 6, and Vite 8, with exact supported versions pinned at scaffolding time. | This is a current, direct development baseline without selecting a component library. | Easy to change before UI slices; increasingly costly after build plugins and tests accumulate. |
| PostgreSQL development version | PostgreSQL 18, pinned to a tested minor image and digest when Compose is created. | It is the current supported major baseline and maximizes support runway for a new project. | Major-version compatibility is operationally significant; patch pins are routine maintenance. |
| Keycloak tested image | Keycloak 26, initially test the current [26.8](https://www.keycloak.org/2026/10/keycloak-2680-released) patch line and pin an exact image/digest. | It provides the intended OIDC identity service while keeping FlowPlane coupled to standards rather than vendor APIs. | Image patches are routine but security-sensitive; dependence on Keycloak-specific APIs would be architecturally significant and is not allowed by this plan. |
| Migration tooling | Use EF Core migrations if the implementation retains the preferred EF Core persistence direction; keep migrations owned and tested by FlowPlane. | It aligns schema changes with the .NET application while supporting reproducible upgrades. | Tooling is moderately changeable early; migration history and production upgrade policy become costly to replace. No ADR is needed unless this changes a broader persistence decision. |
| Test frameworks | Use the current supported .NET test stack selected at scaffolding time, Vitest plus React Testing Library for frontend unit/component tests, and Playwright for a small set of browser journeys. | These provide focused layers without requiring one test tool to cover every boundary. | Easy to change before a substantial test corpus exists; not normally ADR-worthy. |
| Local configuration and secrets | Use ignored local overrides and development secret/environment injection; expose provider credentials through `SecretReference` handling and never commit real values. | It enables Minimal FlowPlane development without requiring an enterprise secret manager. | Configuration mechanics are changeable; the security boundary and prohibition on plaintext persistence/logging are architecturally significant. |
| Backend background-work organization | Initially host scheduling and reconciliation as internally separated ASP.NET Core background responsibilities in the single backend deployable, with PostgreSQL as durable authority. | It is the simplest topology that can prove recovery while retaining a later split point. | Moderately changeable because state is externalized; a new broker, separate authority, or permanent topology commitment requires architectural review. |
| Scheduler mechanism | Select the smallest PostgreSQL-backed recurring mechanism that can meet Slice 11 tests after manual execution is proven; do not select a library during foundation work. | Scheduling requirements should drive the choice, and no additional runtime dependency has yet been justified. | Implementation choice may be replaceable; changes to occurrence identity, durability, or authority are architecturally significant. |

If investigation shows that a gate would change an accepted architectural boundary, stop and use the architecture process. Do not create an ADR for an ordinary library or scaffolding choice whose consequences are local and reversible.

## 32. Explicit non-goals of this plan

This document does not:

- write production code;
- authorize repository files other than this plan;
- define database tables, columns, or relationships;
- define final REST resources or DTOs;
- create Docker Compose files or container definitions;
- create repository CI workflows;
- create migrations;
- create frontend components;
- modify or supersede ADRs;
- expand the agreed MVP scope;
- add Kafka or another unproven infrastructure dependency.

It also does not select future Data-Pipeline Providers, define production topology, or turn technical recommendations into permanent architecture decisions.

## 33. Related documentation

- [Master Architecture and Product Design](../product/FlowPlane-Master-Architecture-and-Product-Design.md)
- [MVP Scope](../product/mvp-scope.md)
- [Architecture Overview](../architecture/overview.md)
- [Data-Pipeline Concepts Research](../domain/data-pipeline-concepts.md)
- [MVP Domain Model](../domain/mvp-domain-model.md)
- [SSIS Provider Technical Design](../architecture/providers/ssis-provider-technical-design.md)
- [ADR 0001: Separate the Control Plane from the Execution Plane](../adr/0001-control-plane-execution-plane-separation.md)
- [ADR 0002: Separate Integration Families](../adr/0002-separate-integration-families.md)
- [ADR 0003: Keep FlowPlane Core Independent and Make External Integrations Additive](../adr/0003-core-independence-and-progressive-integration.md)
- [ADR 0004: Use a Capability-Based Data-Pipeline Provider Architecture](../adr/0004-capability-based-data-pipeline-provider-architecture.md)
- [ADR 0005: Define Execution Lifecycle, Authority, and Reconciliation](../adr/0005-execution-lifecycle-authority-and-reconciliation.md)
