# FlowPlane Vision and Scope

This document is a concise public summary derived from the [master architecture document](FlowPlane-Master-Architecture-and-Product-Design.md), which remains authoritative for overall product and architectural intent.

FlowPlane is in early development. This document describes product direction, not completed functionality or a finalized MVP.

## What FlowPlane is

FlowPlane is an open-source platform intended to provide a centralized control plane for designing, scheduling, executing, and monitoring data workflows across heterogeneous execution environments.

The control plane is intended to own provider-independent workflow and orchestration concerns, while execution technologies retain their technology-specific behavior. This separation allows FlowPlane to coordinate workloads without making one execution engine the foundation of the whole product.

## The problem FlowPlane addresses

Data workflows often span platform-specific runtimes, schedulers, metadata stores, and monitoring tools. That fragmentation makes it difficult to manage workflow definitions, execution requests, schedules, environments, permissions, and operational visibility consistently across execution environments.

FlowPlane is intended to provide a common product and orchestration layer while preserving the native responsibilities of each execution provider.

## Established product scope

The source documents establish the following direction:

- FlowPlane is open source and provider-independent at its core.
- The control plane must be capable of running on Linux and Windows and should be container-friendly.
- The control plane and execution plane are separate architectural concerns.
- FlowPlane owns provider-independent orchestration and application metadata.
- Execution providers own technology-specific runtime behavior and native execution details.
- The platform should begin with a small, useful foundation that can evolve without requiring a rewrite.

These points describe intended product boundaries. They do not define which capabilities are included in the first release.

## The role of SSIS

SQL Server Integration Services (SSIS) is the first major execution provider envisioned for FlowPlane. It is not the platform's architectural foundation.

SSIS-specific artifacts, runtime behavior, parameters, events, messages, and statistics belong behind a provider boundary. The shared FlowPlane domain, API, and user experience should not be modeled as an SSISDB interface.

## Long-term provider vision

The architecture should leave room for additional execution technologies, such as other workflow engines, custom workers, scripts, command-line jobs, Python workloads, and other data-integration systems.

These are future possibilities, not commitments to implement those providers now. Supporting future providers means protecting the provider boundary; it does not mean building a generalized provider ecosystem in the initial release.

## Scope not yet established

The source documents do not yet finalize:

- the exact MVP user journey, inclusions, exclusions, or acceptance criteria;
- the precise provider and capability contracts;
- scheduler, dispatcher, queue, or high-availability mechanisms;
- whether agents are required initially or how they communicate;
- the complete execution state machine and reconciliation rules;
- provider-log ingestion, caching, and retention policy;
- the canonical workflow model or visual-designer architecture;
- SSIS package-authoring strategy;
- authentication, authorization, secret-management, or multi-tenancy mechanisms;
- supported production deployment topologies.

These subjects remain recommendations, assumptions, or open questions until the project accepts specific decisions. The [architecture overview](../architecture/overview.md) summarizes the established architectural boundaries without resolving them.
