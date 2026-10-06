# FlowPlane MVP Domain Model

## Status and authority

This document describes the current MVP domain design derived from the [FlowPlane MVP scope](../product/mvp-scope.md) and the accepted Architecture Decision Records (ADRs). It is not an ADR and does not introduce database schemas, APIs, implementation-language types, or deployment topology.

The [master architecture document](../product/FlowPlane-Master-Architecture-and-Product-Design.md) defines overall product and architectural intent. Accepted ADRs govern the specific decisions they record. The [data-pipeline concepts research](data-pipeline-concepts.md) supplies cross-platform evidence and candidate vocabulary but is not architectural authority.

This model is intentionally limited to the smallest provider-independent domain needed for the SSIS MVP. It does not define the complete future FlowPlane domain.

## Purpose

FlowPlane needs a domain model that can:

- configure and identify the first Data-Pipeline Provider;
- discover provider-native executable definitions without copying their native model into FlowPlane Core;
- register selected definitions as stable FlowPlane Pipelines;
- schedule, execute, cancel, observe, and reconcile Runs;
- preserve FlowPlane orchestration authority separately from provider-native facts;
- support audit and secure credential references;
- project an SSIS Control Flow into a read-only monitoring view.

SSIS is the first implementation proving ground. The model must support SSIS correctly without becoming SSIS-native.

If implementation reveals that a supposedly provider-independent concept is insufficient, the project must:

1. identify the concrete implementation pressure;
2. determine whether it is SSIS-specific or genuinely provider-independent;
3. compare it with the existing cross-platform research;
4. revise general concepts only when broader provider semantics justify the change.

## Design boundaries

The MVP domain follows these accepted boundaries:

- **FlowPlane owns orchestration identity and intent.** Provider-native definitions and executions remain distinct external facts.
- **Provider-native identity is preserved.** FlowPlane uses qualified references instead of replacing provider identifiers with SSIS-shaped core fields.
- **Definition-time and runtime concepts remain separate.** A Pipeline reference is not a Run, and a provider observation is not FlowPlane orchestration state.
- **Optional behavior remains capability-specific.** Graph visualization and detailed diagnostics do not become mandatory internals of every Pipeline.
- **References do not transfer authority.** Storing a native reference or observation in PostgreSQL does not make FlowPlane authoritative for the provider-native object.
- **Names in this document are domain vocabulary, not code contracts.** They do not establish classes, interfaces, tables, columns, API resources, or serialized property names.

## Core MVP domain concepts

### ProviderType

`ProviderType` is the logical identity of a Data-Pipeline Provider implementation or type.

The MVP example is:

```text
ssis
```

The identity lets FlowPlane distinguish SSIS from future provider types without using SSIS terminology as the shared domain model. It does not decide whether `ProviderType` is represented by a database entity, enum, registry record, plugin descriptor, code type, or another mechanism.

### ProviderInstance

`ProviderInstance` is a configured, addressable instance of a Data-Pipeline Provider.

For the SSIS MVP, it represents the configured external SQL Server instance and SSISDB catalog through which FlowPlane discovers, executes, and observes SSIS workloads.

Conceptually, a ProviderInstance may contain or reference:

- a FlowPlane identity;
- its ProviderType;
- a display name;
- provider endpoint and configuration information;
- a `SecretReference`;
- enabled or configured state.

This list does not define persisted fields or connection-string storage.

A ProviderInstance is not an SSISDB Environment. An SSISDB Environment is a provider-native configuration concept whose semantics remain inside the SSIS provider boundary.

### Pipeline

Within the MVP domain, `Pipeline` is a stable FlowPlane-owned, provider-independent identity for a top-level executable data-pipeline workload that FlowPlane manages or references.

For the SSIS MVP, a Pipeline does not contain or own the complete SSIS package definition. It references a provider-native executable definition through `NativeDefinitionReference`.

FlowPlane may associate its own concerns with a Pipeline, including:

- stable FlowPlane identity;
- display name and description;
- ProviderInstance binding;
- NativeDefinitionReference;
- schedules;
- permissions where applicable;
- Run history;
- audit metadata.

This list does not define persisted fields. `Pipeline` is FlowPlane vocabulary; it does not imply that every provider calls its native top-level executable a pipeline or exposes equivalent native semantics.

### Discovery versus registration

Provider discovery and FlowPlane registration are separate operations.

A provider may discover many native executable definitions. Discovery alone must not automatically create FlowPlane Pipelines.

```text
ProviderInstance
        |
        v
Discover native definitions
        |
        v
NativeDefinitionReference candidates
        |
        | selected / imported / registered
        v
FlowPlane Pipeline
```

For SSIS:

```text
SSIS folder / project / package discovery
        |
        v
User selects package
        |
        v
FlowPlane Pipeline references that package
```

The exact user experience, bulk-import policy, synchronization policy, and any future auto-registration behavior remain unresolved.

### NativeDefinitionReference

`NativeDefinitionReference` is a provider-qualified reference to a provider-native executable definition.

For the SSIS MVP, the reference must identify enough of the native hierarchy to address an executable package. The SSIS provider-specific mapping includes:

- SSISDB folder;
- project;
- package.

Those SSIS fields are not established as properties of a universal NativeDefinitionReference schema. They remain provider-specific structured details behind the provider boundary.

Future provider mappings might include an Airflow DAG identifier, an Azure Data Factory pipeline identifier, or another appropriate provider-native identity. These are examples, not provider commitments.

NativeDefinitionReference preserves native identity without making the FlowPlane domain provider-native.

### Schedule

`Schedule` is a FlowPlane-owned recurring execution policy associated with a Pipeline.

The MVP requires a recurring schedule, timezone, enable or disable behavior, and visibility of the next execution. The domain relationship follows [ADR-0005](../adr/0005-execution-lifecycle-authority-and-reconciliation.md):

- `Scheduled` is not a Run lifecycle state;
- a schedule occurrence creates a Run;
- one logical schedule occurrence must create at most one Run.

This document does not define a cron library, scheduler engine, locking mechanism, timezone implementation, or database schema.

### Run

`Run` represents one logical FlowPlane execution intent.

Its provider-independent phase is:

```text
Requested
    |
    v
Queued
    |
    v
Active
    |
    v
Terminal
```

Run outcome is separate from phase. A Terminal Run has one of these conceptual outcomes:

- `Succeeded`;
- `Failed`;
- `Cancelled`;
- `TimedOut`.

During non-terminal phases, outcome is absent. These values are accepted domain semantics, not database enums or API strings.

A Terminal Run is the final FlowPlane orchestration outcome for the logical execution intent. It does not necessarily mean that every associated provider-native execution has terminated. Provider observation and reconciliation may continue after Run terminality, and a later native result does not automatically reopen the Run.

### RunAttempt

`RunAttempt` represents one FlowPlane attempt to fulfill a Run by initiating a top-level native execution through a Data-Pipeline Provider.

Its conceptual lifecycle is:

```text
Created
    |
    v
Dispatching
    |
    v
Submitted
    |
    v
Running
    |
    v
Terminal
```

`Dispatching` is the ambiguous boundary where provider interaction may already have created native execution state but FlowPlane does not yet have sufficient durable evidence of the native execution identity or result. `Submitted` means that FlowPlane has a durable NativeRunReference or equivalent identifying evidence. A provider execution may complete so quickly that FlowPlane observes `Submitted` followed by `Terminal` without observing `Running`; blindly repeating an ambiguous provider-side creation operation is unsafe. The complete accepted lifecycle rationale is in [ADR-0005](../adr/0005-execution-lifecycle-authority-and-reconciliation.md).

A FlowPlane-managed retry creates a new RunAttempt under the same Run. A user-triggered **Run Again** action after a completed logical Run creates a new Run.

Provider-internal retries remain within one FlowPlane RunAttempt when FlowPlane did not initiate another top-level provider execution. Airflow task retries within one DAG Run are an example of provider-internal behavior that must not automatically become FlowPlane RunAttempts.

Provider-oriented terminal attempt outcomes include `Succeeded`, `Failed`, and `Cancelled`. `TimedOut` is a FlowPlane Run policy outcome, not a universal provider-native RunAttempt result.

### NativeRunReference

`NativeRunReference` is the provider-qualified correlation from a RunAttempt to its provider-native execution.

For SSIS, NativeRunReference maps to the native SSIS `execution_id`. The name `execution_id` is SSIS-specific and does not become the generic FlowPlane property name.

The reference enables provider observation, diagnostics, cancellation, recovery, and reconciliation without collapsing FlowPlane and provider-native runtime identity.

### ExecutionContext

`ExecutionContext` is the immutable or effectively immutable snapshot and reference set describing what a Run intended to execute.

Candidate contents include, conceptually:

- Pipeline identity;
- ProviderInstance;
- NativeDefinitionReference;
- native revision or provenance when available;
- trigger information;
- requested parameters;
- provider-native environment or configuration reference where applicable;
- requested-by identity;
- scheduled occurrence;
- provider execution options.

ExecutionContext must preserve historical execution intent even when Pipeline or provider configuration changes later.

ExecutionContext must not contain raw secrets. It may retain a `SecretReference` or another non-secret reference where sensitive material is required at execution time.

This document does not finalize the ExecutionContext schema, exact contents, or immutability mechanism.

### ProviderObservation

`ProviderObservation` represents provider-native runtime facts observed through a Data-Pipeline Provider.

Candidate information includes:

- native state;
- native status code;
- observation time, illustratively `observedAt`;
- native start and end timestamps;
- native result;
- freshness or availability indication;
- provider-specific extension details.

ProviderObservation is conceptually separate from Run and RunAttempt lifecycle state. Provider-native authority remains with the provider even if FlowPlane persists an observation or reference in PostgreSQL.

For the SSIS MVP, [`catalog.executions`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-executions-ssisdb-database?view=sql-server-ver17) is one important observation source. This document does not decide observation storage, retention, polling, caching, or schema.

### Cancellation intent

Cancellation intent is primarily Run or RunAttempt lifecycle information, not necessarily a separate aggregate or entity.

FlowPlane must preserve:

- that cancellation was requested;
- the request time;
- the requesting identity where required;
- an optional reason if later product requirements require one.

Cancellation intent is not a confirmed provider-native result. This document does not decide whether the information is represented by columns, a value object, an event, or an entity.

### Reconciliation information

Reconciliation condition is primarily operational lifecycle information associated with a RunAttempt and provider observation. It is not a separate aggregate by default.

It may need to represent concepts such as:

- reconciliation required;
- reconciliation in progress;
- the last reconciliation attempt;
- provider-observation freshness.

The exact names, persistence, and implementation remain unresolved. Reconciliation completion is distinct from Run terminality.

### SecretReference

`SecretReference` is a core supporting concept representing a reference to protected credential material. It is not the secret value.

ProviderInstance configuration must reference protected credentials rather than store them casually as ordinary plaintext configuration. An external enterprise Secret Integration remains optional for Minimal FlowPlane, but secure credential handling is mandatory.

This document does not select a secret backend, resolution protocol, storage format, or rotation mechanism.

### UserIdentity / ActorReference

`UserIdentity` or `ActorReference` is the working domain label for a minimal FlowPlane reference to the authenticated actor responsible for an action such as:

- registering a Pipeline;
- manually requesting a Run;
- cancelling a Run;
- modifying a Schedule.

For the MVP, authentication uses Keycloak through standard OIDC. FlowPlane must not duplicate the complete Keycloak user model into its domain. It retains a stable FlowPlane or audit reference to the external identity.

The final concept name, identity-mapping rules, RBAC model, and user persistence schema remain unresolved.

### AuditRecord / AuditEvent

`AuditRecord` or `AuditEvent` is the working label for the minimal audit concept required by FlowPlane Core.

It captures security-significant or operationally significant FlowPlane actions where required. It is not the provider execution event log and does not establish event sourcing.

The final name, audit taxonomy, retention, and storage remain later design work.

## GraphProjection

`GraphProjection` is an optional provider capability or projection. It is not a mandatory child of Pipeline and does not make a graph fundamental to every Data-Pipeline Provider.

For the SSIS MVP:

```text
SSIS package
    |
    v
SSIS Provider
    |
    v
GraphProjection
    |
    v
React Flow read-only view
```

The SSIS GraphProjection can represent:

- Control Flow tasks;
- containers;
- precedence constraints.

It can overlay provider runtime observations such as status, duration, warnings, errors, and relevant logs where those observations are available.

The MVP domain does not create canonical core `Node`, `Edge`, or `Task` entities solely to support React Flow. React Flow is a presentation technology, not the canonical backend domain model. This document does not define the GraphProjection schema, SSIS parsing method, runtime-overlay mechanism, or React Flow serialization.

## SSIS MVP native mapping

The mapping below describes the SSIS provider boundary. It does not make SSIS terms part of the provider-independent core.

| FlowPlane concept | SSIS native mapping | Boundary note |
| --- | --- | --- |
| ProviderType | SSIS provider type | FlowPlane identity for the provider implementation; not an SSISDB entity. |
| ProviderInstance | Configured SQL Server and SSISDB instance | FlowPlane configures how to reach the provider; SSISDB remains an external system. |
| Pipeline | FlowPlane registration or reference for an executable SSIS package | FlowPlane owns Pipeline identity but not the package definition. |
| NativeDefinitionReference | SSISDB folder, project, and package identity | The hierarchy remains an SSIS provider-specific mapping. |
| Schedule | FlowPlane-owned recurring policy | It is not a SQL Server Agent schedule. |
| Run | FlowPlane logical execution intent | It remains distinct from an SSIS execution. |
| RunAttempt | One FlowPlane attempt to initiate an SSIS execution | A FlowPlane retry creates another attempt. |
| NativeRunReference | SSIS `execution_id` | SSIS owns the native identifier and execution facts. |
| ExecutionContext | Snapshot of the effective Pipeline/provider reference, invocation inputs, actor, and available SSIS configuration/provenance | It preserves FlowPlane intent without copying raw credentials. |
| ProviderObservation | Observations from public SSISDB catalog views | Native values remain distinguishable from FlowPlane lifecycle state. |
| GraphProjection | SSIS Control Flow representation | Tasks, containers, and precedence constraints are projection details, not universal core entities. |
| Provider diagnostics capability | SSISDB messages, events, and statistics | Diagnostics remain provider-specific or capability-specific detail. |

## SSIS deployment direction

Deployment is not required by the current MVP scope. The material in this section is an implementation direction for a future SSIS provider capability, not a generic Deployment domain decision or an expansion of the MVP.

FlowPlane's Pipeline identity remains stable regardless of how the native SSIS package reached SSISDB. A Pipeline may reference:

1. an existing SSIS package discovered in SSISDB;
2. a package or project later deployed through FlowPlane;
3. a package later generated or edited by a future FlowPlane SSIS designer.

```text
Pipeline
    |
    v
NativeDefinitionReference
    |
    v
SSIS package in SSISDB
```

The source or deployment path does not change Pipeline semantics.

The current SSIS provider technical direction is to:

- use the public SSISDB Transact-SQL catalog API as the primary integration boundary;
- use [`catalog.deploy_project`](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-deploy-project-ssisdb-database?view=sql-server-ver17) for ISPAC or project deployment;
- use [`catalog.deploy_packages`](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-deploy-packages?view=sql-server-ver17) for incremental deployment or update of DTSX packages into an existing project where the target SSIS version supports it;
- use [`catalog.create_execution`](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-create-execution-ssisdb-database?view=sql-server-ver17);
- set execution parameters through public catalog procedures such as `catalog.set_execution_parameter_value`;
- use `catalog.start_execution` to start the native execution;
- use `catalog.stop_operation` to request provider-native cancellation;
- query public SSISDB catalog views for native execution observation and diagnostics.

Microsoft documents that SSISDB objects can be operated through its public [catalog views and stored procedures](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/stored-procedures-integration-services-catalog?view=sql-server-ver17). Its [deployment guidance](https://learn.microsoft.com/en-us/sql/integration-services/packages/deploy-integration-services-ssis-projects-and-packages?view=sql-server-ver17) distinguishes project deployment from incremental package deployment.

An ISPAC is a project deployment artifact implemented using Open Packaging Conventions. It contains one or more DTSX package parts and a project manifest with project and deployment metadata, as described by Microsoft's [ISPAC specification](https://learn.microsoft.com/en-us/openspecs/sql_data_portability/ms-ispac/fa1145dd-120f-4a58-8086-c1d51b2e70f6).

Package-level deployment can support a future FlowPlane SSIS designer without changing the provider-independent Pipeline concept. This direction must be elaborated and verified in a later SSIS Provider Technical Design. This document does not define SQL implementation code, permissions, transactions, compatibility handling, or deployment workflow.

## PipelineVersion

`PipelineVersion` is not a required MVP entity.

For the MVP, this relationship is sufficient:

```text
Pipeline
    |
    v
NativeDefinitionReference
```

At Run time, ExecutionContext may preserve available provider-native revision or provenance. For SSIS, project or version information available from SSISDB may be retained for execution provenance.

PipelineVersion remains future domain work for:

- Git and source-control integration;
- FlowPlane-owned definitions;
- CI/CD integration;
- release;
- deployment;
- promotion;
- immutable FlowPlane-managed versions.

This deferral does not equate a future PipelineVersion with a Git commit or an SSIS project version.

## Environment

The MVP domain does not create a universal FlowPlane `Environment` entity merely because SSIS has SSISDB Environments.

An SSISDB Environment remains provider-native configuration. ExecutionContext may reference the provider-native environment or configuration used for a Run where applicable.

A future generic FlowPlane Environment concept requires separate cross-provider design.

## DataAsset

`DataAsset` is not required by the MVP domain. It remains later lineage, catalog, and cross-provider domain work.

The absence of a generic DataAsset does not prevent provider-specific diagnostics or projections from referring to native data objects where useful.

## Deployment

A generic `Deployment` is not required as a core MVP entity.

SSIS deployment may be added later as a provider capability before FlowPlane defines universal cross-provider Deployment semantics. Adding that capability does not automatically establish `Deployment` as a core aggregate.

## Explicitly deferred from the MVP core

The following concepts are outside the required MVP core domain:

- PipelineVersion;
- generic Environment;
- DataAsset;
- generic Deployment;
- Release;
- Promotion;
- Build;
- BuildArtifact;
- SourceRepository;
- SourceRevision;
- OpenMetadata entities;
- OpenLineage entities;
- generic Graph, Node, and Edge canonical domain;
- Organization and multi-tenancy;
- provider marketplace.

Deferral means that the MVP does not require these concepts. It does not permanently reject them.

## Conceptual relationship model

The following diagram shows conceptual domain relationships. It is not a database schema, aggregate model, set of cardinalities, or serialization contract. `*` means optional or capability-dependent, not a formal cardinality.

```text
ProviderType
    |
    v
ProviderInstance
    |
    +---- SecretReference
    |
    v
Pipeline
    |
    +---- NativeDefinitionReference
    |
    +---- Schedule*
    |
    v
Run
    |
    +---- ExecutionContext
    +---- actor / audit references
    |
    v
RunAttempt
    |
    +---- NativeRunReference
    +---- ProviderObservation
    +---- reconciliation information
    +---- cancellation intent / context

GraphProjection
    |
    +---- optional provider capability associated with Pipeline / Run context
```

## Domain authority and ownership

Authority describes who owns the source fact. It does not prescribe storage location. PostgreSQL may retain FlowPlane state, references, or observations without becoming authoritative for provider-native facts or external identity data.

| Concept or fact | Primary authority | Domain boundary |
| --- | --- | --- |
| ProviderType | FlowPlane | Logical identity used by FlowPlane for a Data-Pipeline Provider type. |
| ProviderInstance | Mixed/reference | FlowPlane owns its configuration record; the external provider owns the addressed installation and native availability. |
| Pipeline identity | FlowPlane | Stable FlowPlane identity for the registered or managed workload. |
| Provider-native definition | Provider | The native platform owns the executable definition and its native hierarchy. |
| NativeDefinitionReference | Mixed/reference | FlowPlane stores the qualified reference; the provider owns the referenced native identity and definition. |
| Schedule | FlowPlane | FlowPlane owns its recurring policy and occurrence intent. |
| Run | FlowPlane | FlowPlane owns logical execution identity, orchestration phase, and outcome. |
| RunAttempt | FlowPlane | FlowPlane owns the attempt identity and orchestration lifecycle. |
| NativeRunReference | Mixed/reference | FlowPlane stores correlation; the provider owns the native execution identity and facts. |
| ExecutionContext | FlowPlane | FlowPlane preserves the historical intent snapshot while referenced native facts retain their original authority. |
| ProviderObservation | Mixed/reference | The provider owns native runtime facts; FlowPlane owns when it observed them and its freshness or availability assessment. Persisting either does not transfer authority over the native execution. |
| Provider-native diagnostics | Provider | Messages, events, statistics, and native component state remain provider facts. |
| Cancellation intent | FlowPlane | FlowPlane owns the request; the provider owns whether and how native cancellation takes effect. |
| Reconciliation information | FlowPlane | FlowPlane owns its operational need and progress while comparing against provider facts. |
| SecretReference | Mixed/reference | FlowPlane stores a non-secret reference; the selected secure mechanism owns protected credential material and resolution. |
| UserIdentity / ActorReference | Mixed/reference | The external identity system owns identity facts; FlowPlane retains a stable action or audit reference. |
| Keycloak identity facts | External identity system | FlowPlane does not duplicate the complete Keycloak model. |
| AuditRecord / AuditEvent | FlowPlane | FlowPlane owns audit records for significant FlowPlane actions. |
| GraphProjection | Mixed/reference | Provider-native structure supplies semantics; FlowPlane exposes a projection for product use. |

## Important design rule

The MVP domain model is implementation-oriented enough to begin the SSIS MVP, but it remains revisable. SSIS is the first proving ground, not the source of universal domain semantics.

When implementation creates pressure to change a provider-independent concept:

- identify the concrete problem;
- determine whether it is SSIS-specific;
- compare it with the existing cross-platform research;
- generalize only when broader provider semantics justify the change.

The project must not over-engineer the domain for hypothetical future providers. It must also not solve an SSIS-specific pressure by silently leaking SSIS terminology or hierarchy into the common model.

## Explicitly not decided

This document does not decide:

- PostgreSQL schema;
- Entity Framework Core entities or configuration;
- REST API resource shapes;
- identifier or UUID formats;
- exact property names;
- repository or service patterns;
- aggregate boundaries;
- a DDD framework;
- event sourcing;
- CQRS;
- provider C# interfaces;
- scheduler implementation;
- reconciliation implementation;
- secret backend;
- RBAC;
- container topology;
- GraphProjection schema;
- SSIS Control Flow parsing method;
- SSIS provider SQL implementation.

## Related documentation

- [FlowPlane Master Architecture and Product Design](../product/FlowPlane-Master-Architecture-and-Product-Design.md)
- [FlowPlane MVP Scope](../product/mvp-scope.md)
- [FlowPlane Architecture Overview](../architecture/overview.md)
- [Data-Pipeline Concepts Research](data-pipeline-concepts.md)
- [ADR 0001: Separate the Control Plane from the Execution Plane](../adr/0001-control-plane-execution-plane-separation.md)
- [ADR 0002: Separate Integration Families](../adr/0002-separate-integration-families.md)
- [ADR 0003: Keep FlowPlane Core Independent and Make External Integrations Additive](../adr/0003-core-independence-and-progressive-integration.md)
- [ADR 0004: Use a Capability-Based Data-Pipeline Provider Architecture](../adr/0004-capability-based-data-pipeline-provider-architecture.md)
- [ADR 0005: Define Execution Lifecycle, Authority, and Reconciliation](../adr/0005-execution-lifecycle-authority-and-reconciliation.md)
