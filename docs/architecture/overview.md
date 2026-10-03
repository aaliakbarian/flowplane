# FlowPlane Architecture Overview

This overview is derived from the [master architecture document](../product/FlowPlane-Master-Architecture-and-Product-Design.md), which remains authoritative for overall product and architectural intent. It also reflects clarified current product direction while keeping unresolved areas open.

FlowPlane is in early development. This document describes intended architecture, not a completed deployment or a finalized MVP.

## System boundary

FlowPlane separates a provider-independent **Control Plane** from a technology-specific **Execution Plane**.

```text
FlowPlane Control Plane
  pipelines, orchestration, scheduling intent, metadata, security, monitoring
                            |
                    provider boundary
                            |
FlowPlane Execution Plane
  data-pipeline platforms, native artifacts, execution, and diagnostics
```

This boundary allows the control plane to coordinate heterogeneous data-pipeline platforms without requiring every provider to share the same runtime, operating system, or native data model.

## Control Plane

The Control Plane owns FlowPlane's provider-independent product and orchestration concerns. These include pipeline definitions and versions, schedules and triggers, execution requests and orchestration state, retries and timeouts, dependencies and concurrency policy, agents or workers, environments and deployment metadata, users and permissions, audit history, provider configuration, monitoring, and alerting.

The control plane is responsible for durable orchestration intent and a consistent product-level view. The exact internal component and deployment boundaries are not yet finalized.

## Execution Plane

The Execution Plane performs work using provider-specific runtimes and infrastructure. It may include platform-specific agents, remote runtimes, or services owned by external data-pipeline platforms.

Data-pipeline providers own technology-specific behavior and native details. For SSIS, those details may include DTSX and ISPAC artifacts, the SSIS runtime, SSISDB operations, provider-specific parameters, messages, events, and data-flow statistics.

The execution plane does not redefine FlowPlane's provider-independent orchestration model, and the control plane does not pretend to own provider-native behavior.

## Data-pipeline provider boundary

FlowPlane places a provider boundary around external data-pipeline platforms. A provider adapter translates between FlowPlane concepts and the provider's native operations while preserving provider-specific identifiers and diagnostics where needed.

SSIS is the first provider and current integration focus. Future providers may include Apache Airflow, dbt, and other appropriate data-pipeline platforms. Airflow and dbt are candidates for investigation, not committed implementations or a fixed delivery order.

Providers may represent different kinds of systems: SSIS is a data-integration and ETL platform, Airflow is a workflow-orchestration platform, and dbt is a data-transformation platform. FlowPlane must not assume that every provider exposes the same concepts, lifecycle, artifacts, or diagnostics.

The core domain, API, and shared user experience must not be modeled as SSISDB tables or other provider-native schemas. Common behavior should remain small and provider-independent, while optional capabilities and extensions represent behavior that is not universal. The precise provider contracts, capability representation, negotiation, and extension model remain unresolved.

## PostgreSQL's intended role

PostgreSQL is intended to store FlowPlane-owned application and orchestration metadata, such as pipeline definitions and versions, schedules, execution requests, orchestration state, users, permissions, audit records, provider configuration, deployment records, and relationships.

PostgreSQL is not intended to replicate a provider database. Provider systems remain authoritative for provider-native facts. For SSIS, SSISDB may remain authoritative for native execution state, operation messages, events, data-flow statistics, and catalog information.

The detailed rules for live queries, caching, copying, streaming, retention, and reconciliation are still open architectural questions.

## Read-only SSIS Control Flow visualization

The intended MVP includes a read-only React Flow visualization of existing SSIS Control Flow structure for monitoring and exploration.

```text
Provider-native pipeline/control-flow representation
                         |
                  Provider adapter
                         |
FlowPlane/provider-independent visualization representation
                         |
                   React Flow view
                         |
              Monitoring / exploration
```

For SSIS, the view is intended to show tasks, containers, and precedence constraints. Where provider data is available, the view may overlay execution state and expose status, duration, warnings, errors, and relevant logs.

React Flow is a presentation and editor technology, not the canonical backend domain model. The MVP direction is read-only monitoring and exploration, not SSIS package authoring, Data Flow design, or editable workflow design. Future editing capabilities may build on this visualization, but they are not an MVP commitment.

The exact provider-independent visualization schema, provider-specific extensions, refresh behavior, and mapping from SSIS artifacts remain unresolved.

## Identity and access direction

Keycloak is the intended MVP identity provider. FlowPlane should integrate with it through standard OpenID Connect (OIDC) rather than coupling the core architecture to Keycloak-specific APIs.

This direction does not yet define detailed authentication flows, token validation and configuration, identity mapping, authorization and RBAC, service identities, agent authentication, session behavior, or multi-tenancy.

## Platform independence

Platform independence applies to the FlowPlane control plane: it must be capable of running on Linux and Windows and should be container-friendly.

Data-pipeline platforms may have operating-system or runtime constraints. FlowPlane must not assume that SSIS or another platform-specific runtime runs in the same process, host, or container as the control plane.

This principle permits cross-platform control-plane deployment while allowing the execution plane to place workloads where their providers can run correctly.

## SSIS as the first provider

SSIS is the first data-pipeline provider envisioned for FlowPlane. It is not the architectural foundation of the platform.

The first-provider role gives FlowPlane a concrete integration target without making SSIS, SSISDB, SQL Server, or SQL Server Agent the shared platform model. Future providers remain possibilities rather than current implementation commitments.

## Architectural areas still unresolved

The source documents and current direction do not yet settle:

- the complete MVP scope, acceptance criteria, and first end-to-end user journey;
- provider contracts, capability schemas, loading, isolation, or compatibility rules;
- scheduler, dispatcher, queue, retry, lease, and high-availability mechanisms;
- agent necessity, placement, transport, registration, authentication, or lifecycle;
- the complete execution state machine, transition authority, attempts, and reconciliation behavior;
- provider-log querying, caching, ingestion, retention, and failure behavior;
- the canonical backend pipeline model, serialization, and schema evolution;
- the provider-independent visualization schema and React Flow mapping;
- SSIS package-authoring and Data Flow design strategy;
- detailed Keycloak/OIDC integration, authorization, RBAC, service identity, secrets, and multi-tenancy mechanisms;
- supported deployment topologies and process boundaries;
- whether any additional infrastructure dependency is required;
- which data-pipeline provider, if any, follows SSIS.

Candidate technologies, future providers, and example interfaces are subjects for evaluation, not accepted choices. Significant accepted decisions should be recorded through the [ADR process](../adr/README.md).
