# ADR 0004: Use a Capability-Based Data-Pipeline Provider Architecture

- **Status:** Accepted
- **Supersedes:** None
- **Superseded by:** None

## Context

[ADR-0001](0001-control-plane-execution-plane-separation.md) established the provider boundary between FlowPlane's provider-independent Control Plane and provider-specific runtimes in the Execution Plane. [ADR-0002](0002-separate-integration-families.md) established Data-Pipeline Providers as one integration family distinct from source control, CI/CD, metadata catalogs, lineage, secrets, and observability. [ADR-0003](0003-core-independence-and-progressive-integration.md) requires FlowPlane Core to remain provider-independent even when only one provider is installed.

This ADR applies only to the internal architectural principles of the **Data-Pipeline Provider** integration family. It does not define the architecture of Source Control, CI/CD, Metadata Catalog, Lineage, Secret, or Observability Integrations.

The [data-pipeline concepts research](../domain/data-pipeline-concepts.md) shows that candidate providers expose materially different models. SSIS packages, Airflow DAGs, ADF pipelines, dbt selections or jobs, NiFi flows, and Airbyte or Fivetran connections are not interchangeable. Their inner units, graphs, schedules, parameters, runtime identities, diagnostics, versioning, deployment, and lineage semantics also differ.

A single broad contract would therefore require unsupported operations or misleading translations. A lowest-common-denominator contract would hide valuable native behavior, while unrelated provider APIs would prevent FlowPlane from offering consistent discovery and execution behavior. FlowPlane needs a small common boundary plus controlled ways to expose non-universal and provider-specific behavior.

## Decision

The proposed decision is that FlowPlane Data-Pipeline Providers use:

1. a small mandatory provider-independent core;
2. optional capability-specific contracts or representations;
3. typed and versioned provider-specific extensions;
4. a raw native-detail fallback only where needed.

This architecture preserves provider-native identities and semantics without leaking them into the mandatory FlowPlane core model. It does not prescribe concrete interfaces, schemas, protocols, or implementation-language types.

### Mandatory provider responsibilities

Every Data-Pipeline Provider must conceptually support the responsibilities below.

#### Provider identity

A provider has a stable provider-type identity, and FlowPlane can distinguish configured provider instances of that type. `ProviderType` and `ProviderInstance` are candidate research concepts; this ADR does not establish their final names, classes, identifiers, persistence, or schemas.

#### Provider-instance validation and connectivity

FlowPlane can determine whether a configured provider instance can be contacted and whether the baseline operations required for that instance are available. This ADR does not define a health-check API, transport, or protocol.

#### Discovery

A provider can discover or address provider-native executable pipeline definitions while preserving native identity. `NativeDefinitionReference` is a candidate research concept, not a finalized domain type.

Examples of native identities include:

- SSIS package and project identity;
- Airflow DAG identity;
- ADF pipeline identity;
- dbt project, job, or executable selection identity;
- Airbyte or Fivetran connection identity where appropriate.

These examples do not imply one universal provider-native hierarchy.

#### Execution

A provider can initiate execution of the supported pipeline definitions it represents. Execution is a defining responsibility of the Data-Pipeline Provider family because FlowPlane's core product purpose includes running and observing data pipelines.

A system that can catalog, index, observe, or describe pipelines but can never execute them should normally belong to another integration family rather than the Data-Pipeline Provider family.

Provider-level execution support does not mean that every configured provider instance, native definition, or runtime situation is executable. Availability may depend on:

- provider-instance configuration;
- credentials and permissions;
- native pipeline state;
- deployment state;
- provider or runtime constraints.

This ADR does not define the execution protocol.

#### Native execution correlation

FlowPlane preserves correlation between FlowPlane-owned runtime identity and provider-native runtime identity. `Run` and `NativeRunReference` are candidate research concepts. This ADR does not finalize the complete Run domain model or execution state machine.

#### Basic execution observation

Every Data-Pipeline Provider must be able to observe enough provider-native execution state for FlowPlane to track and reconcile an execution after it has been initiated.

At minimum, the provider boundary must make it possible to determine the native execution's current or last-known state and whether it is still active or has reached a terminal provider state, where the provider exposes that information.

Providers should preserve relevant native status or result information and timestamps needed for correlation and reconciliation where available.

Basic execution observation is mandatory because FlowPlane's core product responsibility is to run and observe data pipelines.

This decision does not define:

- the FlowPlane Run state machine;
- normalized status values;
- state mappings;
- polling versus streaming;
- refresh intervals;
- caching;
- persistence;
- reconciliation rules;
- retry behavior;
- logs;
- metrics;
- task or unit-level monitoring.

Logs, metrics, detailed diagnostics, graphs, and inner-unit observations remain optional capabilities.

#### Capability description

Every provider exposes enough information for FlowPlane to understand which optional behaviors are supported. This ADR does not define the capability schema or discovery protocol.

### Capability architecture

FlowPlane must not use one large universal provider interface that requires every provider to implement all operations. Non-universal behavior belongs in capability-specific contracts or representations.

Candidate capability areas include:

- cancellation;
- parameters;
- graph inspection and visualization;
- unit-level monitoring;
- logs;
- metrics;
- data assets;
- lineage observations;
- validation;
- deployment;
- artifact access;
- native scheduling;
- version and provenance information.

This list is illustrative, not an approved permanent capability catalog. A capability should be introduced only when multiple real use cases justify meaningful reusable semantics or FlowPlane needs a stable feature boundary.

This ADR does not define `SupportsFoo` boolean fields or any other capability representation.

Capability areas describe optional behavior exposed by a Data-Pipeline Provider. They do not redefine the separate integration families established by ADR-0002. For example, provider-native lineage observations may be exposed by a provider capability without deciding the architecture of a Lineage Integration.

### Contextual capability availability

Capability support must not be assumed to be only a static boolean. FlowPlane must conceptually distinguish among:

- a capability supported by a provider type;
- a capability available for a configured provider instance;
- a capability applicable to a particular native definition;
- an action currently allowed for a specific runtime state.

For example:

```text
SSIS provider type
    cancellation capability exists

SSIS provider instance
    cancellation may be unavailable because of permissions

Run
    cancellation may be unavailable because the run is already terminal
```

This decision establishes only that capability availability can be contextual. It does not define capability negotiation or availability schemas.

### Provider-specific extensions

Provider-specific information that is valuable but not genuinely cross-provider must not expand the mandatory common domain model. FlowPlane uses the following layered conceptual model.

#### Layer 1 — mandatory common model

The mandatory common model contains only genuinely provider-independent identity and execution or correlation semantics.

#### Layer 2 — capability-specific representations

Reusable but non-universal semantics belong to capability-specific representations. Examples include:

- graph inspection;
- logs;
- metrics;
- parameters;
- cancellation;
- data assets;
- deployment.

#### Layer 3 — typed and versioned provider-specific extensions

Provider-specific structured details may be exposed through typed and versioned extensions. Conceptual examples include:

- `ssis.execution-details/v1`;
- `airflow.dag-run-details/v1`;
- `nifi.flow-details/v1`.

These are examples of the principle, not final names or schemas. Provider-specific extensions should:

- be namespaced;
- have an explicit version or schema identity;
- preserve native semantics;
- remain optional for clients that do not understand them.

This ADR does not define a serialization format, JSON Schema layout, storage format, or C# type.

#### Layer 4 — raw native-detail fallback

Raw native provider data may be exposed only as an escape hatch for:

- troubleshooting;
- diagnostics;
- unsupported or newly introduced provider fields;
- forward compatibility.

Raw native payloads must not become the primary FlowPlane API or domain contract. Typed common, capability-specific, or provider-specific representations are preferred when their semantics are understood.

### Definition-time and runtime separation

Provider integrations preserve the distinction between definition-time information and runtime observations.

Definition-time information may include:

- provider-native definition identity;
- parameters and resources;
- optional graph projections;
- optional data-asset projections.

Runtime information may include:

- FlowPlane-owned Run identity;
- provider-native run references;
- execution observations;
- logs;
- metrics;
- optional inner-unit observations.

Provider-native definition objects and runtime state must not be collapsed into one model.

### Graph and visualization

Graph support is optional. A pipeline must not be required to contain one universal graph. Providers may expose graph projections through a graph or visualization capability.

The current MVP example is:

```text
SSIS Control Flow
        |
        v
provider adapter
        |
        v
FlowPlane graph projection
        |
        v
React Flow
```

This ADR does not define `Graph`, `GraphNode`, `GraphEdge`, visualization schemas, or React Flow serialization.

### Native semantics

FlowPlane preserves provider-native concepts when their semantics do not safely generalize. Examples include:

- **SSIS:** Control Flow, Container, Precedence Constraint, and Data Flow;
- **Airflow:** Operator, Sensor, and TaskGroup;
- **NiFi:** Processor, FlowFile, Process Group, and Connection queue;
- **dbt:** Model, Test, and Snapshot.

These concepts may appear through capabilities or provider-specific extensions without becoming mandatory core entities. FlowPlane must not infer a universal Task or Node concept from similar provider terminology.

## Alternatives considered

### One large universal provider interface

Require every provider to implement discovery, execution, cancellation, deployment, scheduling, logs, metrics, graphs, lineage, validation, and other operations. This alternative is not selected because providers expose fundamentally different concepts and capabilities. It would force unsupported operations, misleading implementations, or a continually expanding interface.

### Lowest-common-denominator provider interface

Expose only behavior supported identically by every provider. This alternative is not selected because it would discard valuable provider functionality and weaken FlowPlane's monitoring, exploration, and provider-specific experiences.

### Provider-specific implementations with no common core

Allow each provider to expose unrelated concepts and APIs. This alternative is not selected because FlowPlane would lose a consistent product-level model for provider identity, definition discovery or addressing, execution, runtime correlation, and capability awareness.

### Opaque JSON or native-payload model

Represent most provider information as untyped payloads. This alternative is not selected as the primary model because it would weaken validation, API and UI reuse, compatibility management, and contract testing. Raw native payloads remain available only as a controlled fallback.

### SSIS-centric provider architecture

Use SSIS packages, execution state, Control Flow objects, and SSISDB concepts as the common provider model. This alternative is not selected because SSIS is the first provider, not the architectural foundation. Other data-pipeline platforms must not be required to imitate SSIS.

## Consequences

Positive consequences include:

- FlowPlane preserves a small, stable cross-provider core;
- execution remains fundamental to every Data-Pipeline Provider;
- rich provider behavior remains available;
- future providers do not need to imitate SSIS;
- generic UI and API experiences can use capability-specific representations;
- provider-specific UI and details remain possible;
- providers can evolve without continuously expanding the core domain;
- provider contract testing can distinguish mandatory behavior from optional capabilities.

Trade-offs include:

- capability governance is required;
- capability fragmentation is possible;
- clients must handle unavailable capabilities;
- provider-instance and runtime availability must be distinguished from provider-type support;
- provider-extension versioning adds compatibility work;
- provider adapters require more translation and testing;
- common, capability-specific, and provider-specific boundaries require periodic review.

## Explicitly not decided

This ADR does not decide:

- exact C# interfaces;
- the exact provider API;
- provider plugin loading;
- provider package format;
- process isolation;
- the provider versioning mechanism;
- a capability schema;
- a capability negotiation protocol;
- extension serialization;
- extension storage;
- a Graph schema;
- whether `Pipeline` becomes the canonical term;
- the Run state machine;
- `RunAttempt` semantics;
- the database schema;
- API resource shapes;
- agent architecture;
- scheduler implementation;
- deployment topology;
- a provider SDK;
- contract-test implementation;
- specific Airflow, dbt, or NiFi provider plans.

## Related documentation

- [FlowPlane Master Architecture and Product Design](../product/FlowPlane-Master-Architecture-and-Product-Design.md)
- [FlowPlane MVP Scope](../product/mvp-scope.md)
- [FlowPlane Architecture Overview](../architecture/overview.md)
- [Data-Pipeline Concepts Research](../domain/data-pipeline-concepts.md)
- [ADR 0001: Separate the Control Plane from the Execution Plane](0001-control-plane-execution-plane-separation.md)
- [ADR 0002: Separate Integration Families](0002-separate-integration-families.md)
- [ADR 0003: Keep FlowPlane Core Independent and Make External Integrations Additive](0003-core-independence-and-progressive-integration.md)
