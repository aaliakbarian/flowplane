# ADR 0001: Separate the Control Plane from the Execution Plane

- **Status:** Accepted
- **Supersedes:** None
- **Superseded by:** None

## Context

FlowPlane is intended to be a control plane for heterogeneous data-pipeline platforms. It must provide consistent product and orchestration behavior while allowing each provider to retain its native runtime, artifacts, execution semantics, and platform constraints.

SSIS is the first provider, but it must not become the architectural foundation of FlowPlane. An SSIS-centric core would couple shared domain concepts and product behavior to SSISDB, SSIS artifacts, Windows-specific execution concerns, and other SSIS-native details.

Future provider candidates such as Apache Airflow and dbt represent different categories of data-pipeline platforms and do not expose the same concepts as SSIS or one another. Supporting such providers requires a provider-independent control plane and a boundary that translates between FlowPlane concepts and provider-native behavior.

This is a retrospective ADR candidate for an architectural principle already expressed by the [master architecture document](../product/FlowPlane-Master-Architecture-and-Product-Design.md) and current [architecture overview](../architecture/overview.md). Because its status is Proposed, it is not yet an accepted architectural decision.

## Decision

The proposed decision is to separate FlowPlane into a **Control Plane** and an **Execution Plane**, connected through a provider/adapter boundary.

The **Control Plane** owns provider-independent product and orchestration concerns, including:

- FlowPlane workflow references and versions;
- schedules and orchestration intent;
- execution requests and provider-independent execution state;
- provider configuration and environment metadata;
- product-level security, audit, monitoring, and operational visibility.

The **Execution Plane** contains provider-specific runtimes and infrastructure that perform the actual workload. It owns provider-native behavior and facts, including native artifacts, execution semantics, runtime constraints, native execution identifiers, status, and diagnostics.

Provider adapters translate control-plane requests into provider-native operations and translate native results back into provider-independent state plus provider-specific detail where needed. Provider-native execution remains under the provider and Execution Plane; FlowPlane maintains its own orchestration intent and provider-independent execution state.

FlowPlane and provider-native execution records are distinct and must be correlated. Differences between control-plane state and provider observations require reconciliation rather than treating either representation as interchangeable.

This ADR does not define the provider interface, capability schema, agent architecture, transport protocol, scheduler technology, database schema, process topology, or deployment model. Those remain separate architectural decisions.

## Alternatives considered

### SSIS-centric architecture

Model the FlowPlane core around SSIS packages, SSISDB operations, and SSIS execution behavior. The proposal does not select this alternative because it would make the first provider the platform model, leak SSIS concepts into shared APIs and product behavior, and make future data-pipeline providers expensive to integrate.

### Provider execution inside the core control plane

Load and execute provider-specific runtime behavior directly inside the core control-plane process. The proposal does not select this alternative because provider runtime, operating-system, dependency, and lifecycle constraints would become control-plane constraints and weaken independent deployment and evolution.

### Generic arbitrary-job runner

Represent every workload as an arbitrary command or script instead of defining a provider boundary around data-pipeline platforms. The proposal does not select this alternative because it would discard provider-native concepts and capabilities that FlowPlane needs for discovery, execution, monitoring, and diagnostics. Lower-level job execution may still be useful within an implementation, but it is not the intended provider architecture or product identity.

## Consequences

- The core domain and product experience can remain provider-independent while preserving provider-specific behavior behind adapters.
- SSIS can serve as the first concrete integration without defining the shared model for every future provider.
- Future data-pipeline providers can be added through the provider boundary without requiring them to imitate SSIS.
- Control-plane deployment and evolution are less constrained by provider-specific runtimes and operating systems.
- Each provider requires adapter design, translation logic, validation, and contract testing, increasing integration complexity.
- FlowPlane must retain both its own execution identity and the provider-native execution identity, then correlate and reconcile their state and failure observations.
- Provider capabilities must be described so the control plane does not assume that every provider supports the same operations; the capability model remains to be defined separately.
- Some behavior and diagnostics will remain provider-specific, requiring controlled extensions rather than forcing all providers into a lowest-common-denominator model.

## Related documentation

- [FlowPlane Master Architecture and Product Design](../product/FlowPlane-Master-Architecture-and-Product-Design.md)
- [FlowPlane Architecture Overview](../architecture/overview.md)
- [FlowPlane MVP Scope](../product/mvp-scope.md)
