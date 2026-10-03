# FlowPlane Architecture Overview

This overview is derived from the [master architecture document](../product/FlowPlane-Master-Architecture-and-Product-Design.md), which remains authoritative for overall product and architectural intent. It describes established boundaries without selecting solutions for unresolved areas.

FlowPlane is in early development. This document describes intended architecture, not a completed deployment.

## System boundary

FlowPlane separates a provider-independent **Control Plane** from a technology-specific **Execution Plane**.

```text
FlowPlane Control Plane
  workflows, orchestration, scheduling intent, metadata, security, monitoring
                            |
                    provider boundary
                            |
FlowPlane Execution Plane
  provider runtimes, native artifacts, provider-specific execution and diagnostics
```

This boundary allows the control plane to coordinate heterogeneous workloads without requiring every execution technology to share the same runtime, operating system, or native data model.

## Control Plane

The Control Plane owns FlowPlane's provider-independent product and orchestration concerns. These include workflows and versions, schedules and triggers, execution requests and orchestration state, retries and timeouts, dependencies and concurrency policy, agents or workers, environments and deployment metadata, users and permissions, audit history, provider configuration, monitoring, and alerting.

The control plane is responsible for durable orchestration intent and a consistent product-level view. The exact internal component and deployment boundaries are not yet finalized.

## Execution Plane

The Execution Plane performs work using provider-specific runtimes and infrastructure. It may include platform-specific agents, remote runtimes, or provider services.

Execution providers own technology-specific behavior and native details. For SSIS, those details may include DTSX and ISPAC artifacts, the SSIS runtime, SSISDB operations, provider-specific parameters, messages, events, and data-flow statistics.

The execution plane does not redefine FlowPlane's provider-independent orchestration model, and the control plane does not pretend to own provider-native behavior.

## Provider boundary

SSIS and future execution technologies integrate through a provider boundary. The boundary translates between FlowPlane concepts and provider-native operations while preserving provider-specific identifiers and diagnostics where needed.

The core domain, API, and shared user experience must not be modeled as SSISDB tables or SSIS-specific concepts. Providers may expose optional behavior through capabilities, but the precise provider contracts, capability representation, negotiation, and extension model remain unresolved.

## PostgreSQL's intended role

PostgreSQL is intended to store FlowPlane-owned application and orchestration metadata, such as workflow definitions and versions, schedules, execution requests, orchestration state, users, permissions, audit records, provider configuration, deployment records, and relationships.

PostgreSQL is not intended to replicate a provider database. Provider systems remain authoritative for provider-native facts. For SSIS, SSISDB may remain authoritative for native execution state, operation messages, events, data-flow statistics, and catalog information.

The detailed rules for live queries, caching, copying, streaming, retention, and reconciliation are still open architectural questions.

## Platform independence

Platform independence applies to the FlowPlane control plane: it must be capable of running on Linux and Windows and should be container-friendly.

Execution technologies may have operating-system or runtime constraints. FlowPlane must not assume that SSIS or another platform-specific runtime runs in the same process, host, or container as the control plane.

This principle permits cross-platform control-plane deployment while allowing the execution plane to place workloads where their providers can run correctly.

## SSIS as the first provider

SSIS is the first major execution provider envisioned for FlowPlane. It is not the architectural foundation of the platform.

The first-provider role gives FlowPlane a concrete integration target without making SSIS, SSISDB, SQL Server, or SQL Server Agent the shared platform model. Future providers remain possibilities rather than current implementation commitments.

## Architectural areas still unresolved

The source documents do not yet settle:

- the exact MVP scope and first end-to-end user journey;
- provider contracts, capability schemas, loading, isolation, or compatibility rules;
- scheduler, dispatcher, queue, retry, lease, and high-availability mechanisms;
- agent necessity, placement, transport, registration, authentication, or lifecycle;
- the complete execution state machine, transition authority, attempts, and reconciliation behavior;
- provider-log querying, caching, ingestion, retention, and failure behavior;
- the canonical workflow model, serialization, schema evolution, or React Flow mapping;
- SSIS package-authoring and round-trip strategy;
- authentication, RBAC, service identity, secrets, and multi-tenancy mechanisms;
- supported deployment topologies and process boundaries;
- whether any additional infrastructure dependency is required.

Candidate technologies and example interfaces in the master architecture document are subjects for evaluation, not accepted choices. Significant accepted decisions should be recorded through the [ADR process](../adr/README.md).
