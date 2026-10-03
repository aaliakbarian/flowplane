# FlowPlane MVP Scope

This document formalizes the currently agreed minimum viable product (MVP) direction for FlowPlane. The [master architecture document](FlowPlane-Master-Architecture-and-Product-Design.md) remains authoritative for overall product and architectural intent. The [vision and scope](vision-and-scope.md) and [architecture overview](../architecture/overview.md) provide the surrounding product and system context.

This document owns the MVP objective, inclusions, exclusions, and acceptance criteria. It does not create an Architecture Decision Record (ADR), select unresolved implementation mechanisms, or commit the project to capabilities outside this scope.

## MVP objective

The MVP establishes FlowPlane as an open-source control plane for heterogeneous data-pipeline platforms. It proves that FlowPlane can integrate with an existing platform, represent its pipelines without making that platform the core domain model, execute and monitor its workloads, and provide a foundation for additional data-pipeline providers later.

FlowPlane is not intended to be a generic arbitrary-job runner. Lower-level execution capabilities may be useful in the future, but arbitrary Python, shell, or command-line execution is not the product's primary provider strategy.

## Provider scope

SSIS is the first provider and the only data-pipeline provider included in the MVP. It is a concrete first integration target, not the architectural foundation of FlowPlane.

Apache Airflow, dbt, and other appropriate data-pipeline platforms are future provider candidates. This scope does not commit the project to Airflow or dbt as the next implementation, establish a provider delivery order, or include an additional provider in the MVP.

## Included MVP capabilities

### Identity entry point

- Use Keycloak as the intended MVP identity provider.
- Integrate through standard OpenID Connect (OIDC).
- Keep the FlowPlane core independent of Keycloak-specific APIs.

This inclusion establishes the MVP identity entry point only. Detailed authentication flows, authorization, RBAC, identity mapping, service identities, secret handling, and multi-tenancy remain separate architectural work.

### Existing SSIS environment and package discovery

- Connect FlowPlane to an existing SSIS environment.
- Discover existing SSIS projects and packages.
- Create FlowPlane workflow references to existing SSIS packages.

The MVP works with existing SSIS assets. Creating a FlowPlane reference does not make FlowPlane the owner or author of the underlying SSIS package.

### SSIS execution and cancellation

- Manually request execution of a referenced SSIS package.
- Cancel an execution when the native provider operation supports cancellation.
- Track provider-independent FlowPlane execution state.
- Correlate each FlowPlane execution with its native SSIS execution identifier.

The exact execution state machine, transition authority, cancellation races, retry behavior, idempotency mechanism, and reconciliation rules remain unresolved technical decisions.

### Execution history and diagnostics

- Display FlowPlane execution history for referenced SSIS packages.
- Display provider-native execution status where available.
- Display relevant provider-native diagnostics and execution logs, including warnings and errors where available.

The MVP requirement is visibility into relevant execution information. It does not decide whether provider-native data is queried live, cached, copied, streamed, or retained by a particular mechanism.

### Read-only SSIS Control Flow visualization

The MVP includes a **read-only** SSIS Control Flow visualization using React Flow. The view should visualize the following when the selected package and its runtime information provide the corresponding data:

- tasks;
- containers;
- precedence constraints;
- execution state;
- task or executable status;
- duration;
- warnings and errors;
- relevant execution logs.

The intended representation path is:

```text
SSIS package/control-flow representation
                  |
        SSIS provider adapter
                  |
 FlowPlane visualization representation
                  |
          React Flow view
                  |
      Monitoring / exploration
```

This is a monitoring and exploration feature. It is not an SSIS package designer, an SSIS Data Flow Designer, or an editable FlowPlane workflow designer. React Flow is not the canonical backend workflow or domain model.

Future editable design capabilities may build on this work, but they are not included in the MVP. The exact visualization schema, provider-extension model, artifact parsing approach, runtime overlay mechanism, and refresh behavior remain unresolved.

### Basic recurring scheduling

The MVP includes basic recurring scheduling with:

- a recurring schedule;
- a timezone;
- enable and disable controls;
- visibility of the next execution.

This scope establishes product behavior, not the scheduling implementation. Scheduler libraries, persistence and locking mechanisms, concurrency behavior, and detailed timezone or daylight-saving handling remain unresolved unless required to satisfy the basic acceptance journey.

## MVP acceptance criteria

The MVP is accepted when the following end-to-end journey can be demonstrated:

1. A user authenticates to FlowPlane through Keycloak using standard OIDC.
2. The user connects FlowPlane to an existing SSIS environment.
3. FlowPlane discovers an existing SSIS project and package.
4. The user creates a FlowPlane workflow reference to that existing package.
5. The user manually requests execution of the referenced package.
6. FlowPlane tracks the execution and correlates it with the native SSIS execution identifier.
7. Across suitable acceptance executions, the user observes both FlowPlane execution state and native SSIS execution status, together with relevant provider-native diagnostics and execution logs, including warnings and errors.
8. The user can cancel an appropriate in-progress execution and observe the resulting state.
9. The user reviews execution history for the referenced package.
10. Using a representative SSIS package containing tasks, at least one container, and precedence constraints, the user opens the package's Control Flow in the read-only React Flow monitoring view and inspects that structure. Across suitable acceptance executions, the view also demonstrates execution state, task or executable status, duration, warnings, errors, and relevant execution logs.
11. The user configures and enables a basic recurring schedule with a timezone, sees its next execution, and observes at least two successive automatic executions with history and native SSIS identifier correlation. After the user disables the schedule, no new scheduled execution is requested; re-enabling it restores the next-execution indication.

The journey validates the agreed product slice. It does not imply that unresolved architectural mechanisms or advanced operational behavior have been selected.

## Explicit MVP non-goals

The following are not part of the MVP:

- SSIS package authoring;
- an SSIS Data Flow Designer;
- editable React Flow workflows;
- an Airflow provider;
- a dbt provider;
- additional data-pipeline providers;
- arbitrary Python or shell execution as a provider strategy;
- a distributed agent architecture;
- Kafka;
- RabbitMQ;
- Temporal;
- Kubernetes;
- a high-availability scheduler;
- leader election or a distributed scheduler;
- complex misfire policies;
- event triggers;
- complex DAG scheduling;
- distributed queues;
- multi-tenancy;
- a complex deployment or promotion system;
- a provider marketplace or ecosystem.

These exclusions constrain the MVP; they do not permanently reject future capabilities or technologies. Any future addition requires its own product and architectural review.

## Future possibilities

After the SSIS integration boundary is proven, the project may investigate Airflow, dbt, and other appropriate data-pipeline platforms as provider candidates. No candidate is selected as the next provider by this document.

Editable SSIS or FlowPlane design experiences may also build on the read-only visualization foundation later. Package authoring, Data Flow design, and editable React Flow workflows remain outside the MVP.

Lower-level Python, shell, or command-line execution may be considered as an implementation capability where appropriate, but it is not the intended provider ecosystem or product identity.

## Remaining unresolved technical decisions

The MVP scope does not decide:

- the detailed authentication flow, token validation configuration, authorization model, RBAC scope, identity mapping, service identities, secrets architecture, or multi-tenancy model;
- the provider interface, capability model, extension format, loading model, isolation model, or compatibility rules;
- the supported SSIS connection and execution modes or the detailed SSIS adapter contract;
- the canonical backend workflow or pipeline model, database schema, API resource shapes, or versioning strategy;
- the execution state machine, attempt model, transition authority, idempotency mechanism, cancellation races, retry policy, or reconciliation behavior;
- whether provider-native history, diagnostics, and logs are queried live, cached, copied, streamed, or retained by FlowPlane;
- the provider-independent visualization schema, SSIS artifact mapping, runtime-overlay mechanism, refresh model, or provider-specific visualization extensions;
- the scheduler library, persistence model, locking model, concurrency rules, misfire behavior, or high-availability evolution path;
- whether an agent is required for the MVP, where provider operations run, or which communication transport would be used;
- the production deployment topology, process boundaries, or infrastructure dependencies.

Those decisions require separate architectural work and, where architecturally significant, accepted ADRs. This document must not be used to infer a technology selection beyond the explicitly agreed MVP direction above.
