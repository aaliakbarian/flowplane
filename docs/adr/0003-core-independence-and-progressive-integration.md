# ADR 0003: Keep FlowPlane Core Independent and Make External Integrations Additive

- **Status:** Accepted
- **Supersedes:** None
- **Superseded by:** None

## Context

[ADR-0001](0001-control-plane-execution-plane-separation.md) separates FlowPlane's provider-independent Control Plane from the provider-specific runtimes and infrastructure in the Execution Plane. [ADR-0002](0002-separate-integration-families.md) distinguishes Data-Pipeline Providers from source control, CI/CD, metadata catalog, lineage, secret, and observability integrations.

Those boundaries leave a related question: which integrations must be present for FlowPlane to be a useful product? If Git, CI/CD, enterprise secret management, external observability, metadata catalogs, or lineage systems became baseline prerequisites, small installations would inherit unnecessary operational dependencies. If the first provider defined a reduced “simple” architecture, FlowPlane would become SSIS-centric and later installations would need to migrate to a different domain model or product shape.

FlowPlane therefore needs one provider-independent architectural core that supports both a minimal installation and progressively richer integrations. This decision concerns that invariant; it does not prescribe the physical implementation or runtime topology of the core.

## Decision

FlowPlane is one product with one provider-independent architectural core. A useful FlowPlane deployment must not require optional enterprise integrations. The provider-independent FlowPlane Core remains the same architecture whether an installation uses only SSIS or adds multiple providers and external integrations.

Optional integration families extend FlowPlane; they do not define FlowPlane Core and must not become prerequisites for using FlowPlane with a Data-Pipeline Provider.

### Core principle

FlowPlane Core is a logical product and architectural boundary. It is not a decision about:

- code modules;
- services;
- processes;
- deployment units;
- runtime topology.

FlowPlane Core must remain provider-independent even when only one Data-Pipeline Provider is installed. Conceptually:

```text
FlowPlane Core
      |
      v
provider-independent domain
      |
      v
Provider Boundary
      |
      v
SSIS Provider
      |
      v
SSIS / SSISDB
```

The FlowPlane domain must not become SSIS-native or SSIS-centric.

### Minimal FlowPlane

Minimal FlowPlane is the simplest useful deployment and integration level of the same FlowPlane product. Conceptually, it requires:

- the FlowPlane Web UI;
- the FlowPlane API;
- the provider-independent domain;
- PostgreSQL;
- authentication and authorization;
- scheduling;
- durable execution state and history;
- built-in application and execution monitoring;
- health information;
- audit;
- secure credential and `SecretReference` handling;
- at least one Data-Pipeline Provider.

For the MVP and reference deployment:

- PostgreSQL is the FlowPlane metadata database;
- Keycloak is the intended identity provider through standard OpenID Connect (OIDC);
- SSIS is the first Data-Pipeline Provider.

These product choices do not decide the detailed internal implementation of those capabilities.

### Optional integrations

Minimal FlowPlane must not require:

- Git;
- a Source Control Integration;
- a CI/CD Integration;
- a Metadata Catalog Integration;
- a Lineage Integration;
- an external Observability Integration;
- an external enterprise Secret Integration.

#### Observability

FlowPlane Core must provide the operational capabilities required by the product, including:

- application logging;
- health information;
- execution status;
- execution history;
- provider diagnostics exposed through provider integrations.

External observability platforms are optional extensions. Their absence does not make the product's built-in operational capabilities optional.

#### Secrets

Secure credential handling is mandatory. External secret-management platforms are optional.

FlowPlane must support secure `SecretReference` handling without requiring a separate enterprise secrets product merely to operate Minimal FlowPlane. This decision does not select a secret-storage implementation.

#### Source control

Git is optional. Provider-native definitions, including existing SSIS packages, must remain usable when no Git repository is linked.

FlowPlane-owned version and runtime identity must not depend on Git identity.

### Progressive Integration

Optional integrations enrich FlowPlane; they do not become prerequisites for FlowPlane Core. FlowPlane supports additive integration levels around the same core:

#### Minimal FlowPlane

FlowPlane Core, required core infrastructure, and one or more Data-Pipeline Providers. No optional external integration family is required.

#### Source-linked FlowPlane

Minimal FlowPlane with an optional Source Control Integration.

#### Enterprise-integrated FlowPlane

The same FlowPlane product may additionally integrate with:

- CI/CD systems;
- metadata catalogs;
- lineage systems;
- external secret-management systems;
- external observability systems;
- approval and promotion systems;
- other enterprise integrations.

These integration levels are not separate products, product editions, separate architectures, or incompatible operating modes. They are additive integrations around the same FlowPlane Core.

## Alternatives considered

### Require Git or source control for every FlowPlane pipeline

This would provide a uniform provenance assumption but would exclude provider-native assets that are not linked to Git, including existing SSIS packages. It would also make an external source-control system a prerequisite for basic FlowPlane usage and incorrectly equate FlowPlane version identity with Git identity.

### Require enterprise integrations in the baseline installation

Requiring CI/CD, metadata, lineage, external observability, or enterprise secret-management platforms would increase installation and operational cost before those integrations provide value to a given deployment. It would prevent a useful minimal installation and blur the boundaries established by ADR-0002.

### Allow SSIS to define the core domain for simple installations

An SSIS-native core could shorten the first integration path, but it would make the initial provider the platform foundation. Adding other Data-Pipeline Providers would then require incompatible abstractions or a migration to a different core model, contrary to ADR-0001.

### Maintain separate simple and enterprise FlowPlane architectures

Separate architectures could optimize each installation profile independently, but they would fragment the domain model, APIs, operational behavior, and evolution path. Users would face a product migration rather than adding integrations as their needs grow.

## Consequences

Positive consequences include:

- simpler installation and onboarding;
- preservation of provider independence from the first deployment;
- no enterprise dependency tax for small deployments;
- enterprise capabilities can be added without migrating to a different product;
- the same domain model and APIs can evolve across deployment sizes.

Trade-offs include:

- FlowPlane must provide baseline logging, health, security, audit, scheduling, and durable state rather than delegate all of them to external platforms;
- optional integrations create multiple operating combinations that require testing;
- internal and external responsibility boundaries must remain explicit;
- integration capabilities must degrade gracefully when optional systems are absent.

## Explicitly not decided

This ADR does not decide:

- service or process topology;
- modular monolith versus microservices;
- the exact authentication architecture;
- authorization or RBAC design;
- a secret backend;
- an observability backend;
- OpenTelemetry adoption;
- a Git provider;
- a CI/CD provider;
- a metadata catalog provider;
- a lineage provider;
- API contracts;
- the database schema;
- deployment topology;
- the internal architecture of Data-Pipeline Providers.

## Related documentation

- [FlowPlane Master Architecture and Product Design](../product/FlowPlane-Master-Architecture-and-Product-Design.md)
- [MVP Scope](../product/mvp-scope.md)
- [Architecture Overview](../architecture/overview.md)
- [ADR-0001: Separate the Control Plane from the Execution Plane](0001-control-plane-execution-plane-separation.md)
- [ADR-0002: Separate Integration Families](0002-separate-integration-families.md)
- [Data-Pipeline Concepts Research](../domain/data-pipeline-concepts.md)
