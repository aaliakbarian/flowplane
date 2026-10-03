# FlowPlane Vision and Scope

This document is a concise public summary derived from the [master architecture document](FlowPlane-Master-Architecture-and-Product-Design.md), which remains authoritative for overall product and architectural intent. It also reflects clarified current product direction without modifying the master document.

FlowPlane is in early development. This document describes product direction, not completed functionality or a finalized MVP.

## What FlowPlane is

FlowPlane is an open-source control plane for heterogeneous data-pipeline platforms. It is intended to provide a centralized way to discover, schedule, execute, and monitor pipelines across systems with different runtime models and platform constraints.

The control plane owns provider-independent workflow and orchestration concerns, while data-pipeline providers retain their technology-specific behavior. This separation allows FlowPlane to coordinate pipelines without making one provider the foundation of the whole product.

## The problem FlowPlane addresses

Data pipelines often span platform-specific runtimes, schedulers, metadata stores, and monitoring tools. That fragmentation makes it difficult to manage pipeline definitions, execution requests, schedules, environments, permissions, and operational visibility consistently across platforms.

FlowPlane is intended to provide a common product and orchestration layer while preserving the native responsibilities of each data-pipeline provider.

## Provider categories

A FlowPlane provider represents an external data-pipeline platform. Different providers may represent different categories of systems and do not need to expose identical concepts.

Examples include:

- SSIS as a data-integration and ETL platform;
- Apache Airflow as a workflow-orchestration platform;
- dbt as a data-transformation platform.

SSIS is the first concrete provider. Airflow and dbt are future provider candidates, not commitments about which provider will be implemented next.

Arbitrary Python scripts, shell scripts, and command-line jobs are not the primary future provider strategy. They may remain possible lower-level execution capabilities where appropriate, but the broader product direction centers on integrating data-pipeline platforms.

## Established product boundaries

- FlowPlane is open source and provider-independent at its core.
- The control plane must be capable of running on Linux and Windows and should be container-friendly.
- The control plane and execution plane are separate architectural concerns.
- FlowPlane owns provider-independent orchestration and application metadata.
- Data-pipeline providers own technology-specific runtime behavior and native execution details.
- The platform should begin with a small, useful foundation that can evolve without requiring a rewrite.

These points describe intended product boundaries. They do not define every capability or acceptance criterion for the first release.

## The role of SSIS

SQL Server Integration Services (SSIS) is the first data-pipeline provider envisioned for FlowPlane. It is not the platform's architectural foundation.

SSIS-specific artifacts, runtime behavior, parameters, events, messages, and statistics belong behind a provider boundary. The shared FlowPlane domain, API, and user experience should not be modeled as an SSISDB interface.

## Intended MVP direction

The current intended MVP direction includes:

- authentication through Keycloak using standard OpenID Connect (OIDC), without coupling the core architecture to Keycloak-specific APIs;
- discovery of existing SSIS packages and the ability to request their execution;
- execution monitoring and access to relevant provider-native status and diagnostics;
- a read-only SSIS Control Flow visualization using React Flow.

The visualization is intended to show existing tasks, containers, and precedence constraints. Where available, it should overlay execution state and present information such as status, duration, warnings, errors, and relevant logs.

This is a monitoring and exploration feature, not an SSIS package designer. React Flow is a presentation technology for the view and must not become the canonical backend workflow model.

This direction does not finalize the full MVP scope, delivery sequence, deployment topology, or acceptance criteria.

## Future possibilities outside the intended MVP

SSIS package authoring, a Data Flow Designer, and editable React Flow workflows are future possibilities rather than MVP commitments. Future editing or design features may build on the read-only visualization capability, but their model and architecture remain unresolved.

Additional data-pipeline providers may also be investigated after the SSIS boundary is proven. Airflow, dbt, and other appropriate platforms are examples, not a committed roadmap.

## Scope not yet established

The source documents and current direction do not yet finalize:

- the complete MVP user journey, inclusions, exclusions, or acceptance criteria;
- the precise provider and capability contracts;
- scheduler, dispatcher, queue, or high-availability mechanisms;
- whether agents are required initially or how they communicate;
- the complete execution state machine and reconciliation rules;
- provider-log ingestion, caching, and retention policy;
- the canonical workflow model or future visual-designer architecture;
- SSIS package-authoring and Data Flow design strategy;
- authorization, secret-management, service-identity, or multi-tenancy mechanisms;
- detailed Keycloak/OIDC integration and identity-mapping behavior;
- supported production deployment topologies;
- which data-pipeline provider, if any, follows SSIS.

These subjects remain recommendations, assumptions, or open questions until the project accepts specific decisions. The [architecture overview](../architecture/overview.md) summarizes the established architectural boundaries and current intended direction without resolving those questions.
