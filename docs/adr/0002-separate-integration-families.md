# ADR 0002: Separate Integration Families

- **Status:** Accepted
- **Supersedes:** None
- **Superseded by:** None

## Context

[ADR-0001](0001-control-plane-execution-plane-separation.md) established the boundary between FlowPlane's provider-independent Control Plane and the provider-specific runtimes and infrastructure in the Execution Plane. FlowPlane also needs to integrate with external systems that support source management, delivery, metadata, lineage, secrets, and observability. Those systems do not share the role or semantics of a data-pipeline platform.

The [data-pipeline concepts research](../domain/data-pipeline-concepts.md) identifies materially different responsibilities:

- SSIS, Airflow, dbt, NiFi, Azure Data Factory, Dagster, and other suitable platforms define, execute, orchestrate, transform, ingest, or observe data-pipeline work. They are data-pipeline platforms, although their native models are not equivalent.
- GitHub, GitLab, Forgejo, Gitea, and generic Git repositories manage source, revisions, references, and provenance.
- GitHub Actions, GitLab CI, Forgejo Actions, Jenkins, Tekton, and other CI/CD systems manage validation, builds, artifacts, approvals, and delivery workflows.
- OpenMetadata is oriented toward catalogs, metadata, discovery, ownership, tags, status, and lineage relationships. It is not an execution provider for the pipelines it catalogs.
- OpenLineage is oriented toward interoperable lineage events and metadata about jobs, runs, and datasets. It does not own pipeline execution or FlowPlane orchestration.
- Secret systems protect and resolve credential material, while observability systems collect or associate logs, metrics, traces, alerts, and operational telemetry.

These systems have different authority, identity, lifecycle, security, and failure semantics. Treating them all as implementations of one generic Provider abstraction would hide those differences and blur ownership boundaries.

## Decision

FlowPlane will distinguish semantic integration families rather than force every external integration through one universal Provider abstraction.

The conceptual integration families are:

- **Data-Pipeline Provider** — integration with an external data-pipeline platform. SSIS is the first provider; other platforms remain future candidates.
- **Source Control Integration** — integration with external repositories, revisions, references, and source provenance.
- **CI/CD Integration** — integration with external validation, build, artifact, release, approval, and delivery workflows.
- **Metadata Catalog Integration** — integration for catalog metadata, discovery, ownership, tags, status, and entity references.
- **Lineage Integration** — integration for lineage observations and relationships.
- **Secret Integration** — integration for protected credential references and resolution.
- **Observability Integration** — integration for operational logs, metrics, traces, alerts, and telemetry.

The family names are architectural terminology, not prescribed interface, plugin, API, or C# contract names. This decision classifies data-pipeline providers as one integration family but does not decide their internal architecture.

Cross-family workflows must coordinate the participating families explicitly instead of depending on a shared generic interface.

Optional integration families extend FlowPlane; they do not define the FlowPlane core and must not become prerequisites for using FlowPlane with a Data-Pipeline Provider.

Integration adoption may progress around the same FlowPlane Core as described below.

### Minimal FlowPlane

FlowPlane Core is the provider-independent architectural core shared by every FlowPlane deployment. FlowPlane Core is a logical product and architecture boundary, not a decision about code modules, processes, services, deployment units, or runtime topology. Minimal FlowPlane is the simplest supported deployment and integration level. It remains one FlowPlane product and one architecture.

```text
Minimal FlowPlane
|
+-- FlowPlane Core
|   +-- provider-independent domain
|   +-- Web UI
|   +-- API
|   +-- scheduler
|   +-- durable execution state/history
|   +-- built-in monitoring/health
|   +-- audit
|   +-- secure credential/secret-reference handling
|
+-- PostgreSQL
+-- authentication/authorization
+-- at least one Data-Pipeline Provider
    +-- initially SSIS
```

For the MVP/reference deployment:

- PostgreSQL is the FlowPlane metadata database;
- Keycloak is the intended identity provider through standard OpenID Connect (OIDC);
- SSIS is the first Data-Pipeline Provider.

The exact internal implementation of these capabilities remains subject to their respective architecture decisions.

Minimal FlowPlane must not require:

- Git or any Source Control Integration;
- CI/CD integration;
- Metadata Catalog Integration;
- Lineage Integration;
- an external Observability Integration;
- an external enterprise secret-management integration.

#### Built-in operational observability

Optional Observability Integration does not mean FlowPlane can operate without observability. FlowPlane Core must provide the built-in operational capabilities required by its product scope, including:

- application logging;
- health information;
- execution status;
- execution history;
- provider-native diagnostics exposed through the provider integration.

External observability systems are optional extensions.

#### Credential security

Optional Secret Integration does not mean credential security is optional. FlowPlane must securely handle credentials and secret references in Minimal FlowPlane. An external secret-management platform may be integrated later, but it must not be mandatory merely to run FlowPlane with SSIS.

#### Provider-independent identity and optional source control

FlowPlane must retain version and runtime identity where required by its provider-independent domain. Git is optional. Provider-native assets must remain supported even when they have no FlowPlane-linked Git repository.

```text
FlowPlane Core
        |
provider-independent domain
        |
Provider Boundary
        |
SSIS Provider
        |
SSIS / SSISDB
```

Existing SSIS packages can be referenced through this boundary for FlowPlane management, execution, and monitoring without Git, CI/CD, OpenMetadata, or OpenLineage. The FlowPlane Core remains provider-independent and does not become SSIS-native or SSIS-centric.

#### Progressive Integration

Progressive Integration describes additive integration levels around the same FlowPlane Core:

1. **Minimal FlowPlane**

   - FlowPlane Core;
   - required core infrastructure;
   - one or more Data-Pipeline Providers;
   - no optional external integrations required.

2. **Source-linked FlowPlane**

   - adds an optional Source Control Integration.

3. **Enterprise-integrated FlowPlane**

   - may additionally add CI/CD, Metadata Catalog, Lineage, Secret, Observability, approval/promotion, and other enterprise integrations.

These levels are not separate products, editions, architectures, or incompatible modes. They are additive integration levels around the same FlowPlane Core. Optional integrations may be added around that core; they do not replace Minimal FlowPlane or redefine the core architecture.

## Alternatives considered

### One universal integration Provider abstraction

Represent data-pipeline platforms, source-control systems, CI/CD systems, catalogs, lineage systems, secret systems, and observability systems through one generic Provider abstraction. This alternative is not selected because it would conflate fundamentally different authority, identity, security, lifecycle, and failure semantics. The resulting abstraction would either be excessively broad or provide little meaningful common behavior.

### Treat source control and CI/CD as data-pipeline providers

Model Git systems and delivery systems as providers alongside SSIS and other data-pipeline platforms. This alternative is not selected because source-control systems own source and provenance, while CI/CD systems own validation, build, artifact, and delivery workflows. Neither role is equivalent to defining or executing work in a data-pipeline platform.

### Treat metadata and lineage systems as data-pipeline providers

Model OpenMetadata, OpenLineage, and similar systems as data-pipeline providers. This alternative is not selected because catalogs and lineage systems describe, ingest, or exchange metadata and observations; they do not thereby become authoritative for provider-native execution or FlowPlane orchestration.

### Completely ad-hoc integrations with no integration-family taxonomy

Allow each external integration to define unrelated boundaries without a shared semantic classification. This alternative is not selected because it would obscure ownership, encourage inconsistent terminology, and make security, lifecycle, and cross-integration coordination harder to reason about. The family taxonomy provides boundaries without requiring one universal implementation contract.

## Consequences

- Integration ownership and authority boundaries are clearer because systems with different responsibilities are not presented as interchangeable providers.
- Each integration family can evolve according to its own identity, lifecycle, security, compatibility, and failure concerns.
- Minimal FlowPlane can use provider-native assets through the provider boundary without requiring Git hosting, CI/CD, catalogs, lineage systems, external secret managers, observability platforms, or enterprise delivery infrastructure.
- Enterprise-integrated FlowPlane can add source-control, CI/CD, metadata, lineage, secret, and observability integrations progressively when its use cases require them.
- FlowPlane will eventually require more than one integration abstraction where those families are implemented; a single generic Provider contract will not cover every external-system relationship.
- Cross-family workflows will require explicit coordination and authority rules rather than relying on operations inherited from a shared generic interface.
- The taxonomy requires governance so that new integrations are assigned by semantics rather than convenience or similar terminology.

## Explicitly not decided

This ADR does not decide:

- data-pipeline provider interfaces or internal provider architecture;
- capabilities, capability schemas, or capability discovery;
- plugin contracts, loading, versioning, or process isolation;
- C# interfaces or other implementation-language contracts;
- visualization architecture or visualization projections;
- lineage projection architecture;
- whether `Pipeline` or another term becomes canonical;
- Git or source-control provider selection;
- CI/CD provider selection;
- metadata catalog product selection;
- lineage backend selection;
- source-control or Git integration protocols;
- CI/CD integration protocols;
- secret backends or secret-resolution protocols;
- observability backends or telemetry protocols;
- APIs, database schemas, agent architecture, transport protocols, scheduler implementation, or deployment topology.

These matters require separate architecture or technical-design work. In particular, the internal architecture of the Data-Pipeline Provider family remains a separate architectural decision.

## Related documentation

- [FlowPlane Master Architecture and Product Design](../product/FlowPlane-Master-Architecture-and-Product-Design.md)
- [FlowPlane MVP Scope](../product/mvp-scope.md)
- [FlowPlane Architecture Overview](../architecture/overview.md)
- [ADR 0001: Separate the Control Plane from the Execution Plane](0001-control-plane-execution-plane-separation.md)
- [Data-Pipeline Concepts Research](../domain/data-pipeline-concepts.md)
