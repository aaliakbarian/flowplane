# FlowPlane Project Understanding

## Document status

This document records a newcomer’s current understanding of FlowPlane. It is derived primarily from the master product and architecture brief, `docs/product/FlowPlane-Master-Architecture-and-Product-Design.md`, plus the repository `README.md`.

The brief asks many questions and proposes several candidate designs; it is not itself a complete, approved architecture specification. To avoid turning proposals into decisions, this document uses these labels:

- **Existing decision**: explicitly stated product direction, boundary, principle, or constraint.
- **Assumption**: a working interpretation that still needs validation.
- **Recommendation**: a suggested next step, not approved architecture.
- **Open question**: a material choice that the current source does not settle.

The canonical architecture reference is `docs/product/FlowPlane-Master-Architecture-and-Product-Design.md`.

## 1. Product vision summary

### Existing decisions

FlowPlane is intended to be an open-source, cross-platform platform for designing, scheduling, executing, and monitoring data workflows across heterogeneous execution environments. Its enduring product identity is a centralized control plane, not a replacement web interface tied to one integration technology.

SSIS is the first major execution provider, but it must not become FlowPlane’s core domain model or architectural foundation. Future execution technologies may include workflow engines, agents, scripts, command-line jobs, Python workloads, and other integration systems; the MVP does not need to implement these, but it must avoid making them impossible.

The intended outcome is a small, clean, useful MVP with an evolutionary path to a serious platform. The project explicitly rejects building the full distributed system, designer, and provider ecosystem before validating the core.

### Assumptions

- The first useful vertical slice will likely demonstrate centralized visibility or execution of an SSIS workload, because SSIS is the first named provider.
- “Cross-platform” applies to the FlowPlane control plane. It does not imply that every execution technology or agent capability works on every operating system.
- Heterogeneous execution is a long-term extensibility requirement, not an MVP feature-count requirement.

### Recommendations

- Convert the master brief’s firm statements into Architecture Decision Records (ADRs), leaving its questions as explicit decision backlog items.
- Define one measurable MVP user journey before expanding the roadmap, so “minimal working platform” has an objective completion criterion.

## 2. Main architectural principles

### Existing decisions

1. **Separate the control plane from the execution plane.** FlowPlane orchestrates; provider runtimes execute in environments appropriate to their platform requirements.
2. **Keep the core provider-independent.** SSIS-specific concepts must not leak into the shared workflow, scheduling, execution, API, or UI models.
3. **Make the control plane cross-platform and container-friendly.** It must be capable of running on Linux and Windows and fit Docker-oriented deployments, with Kubernetes as a possible deployment environment rather than an MVP requirement.
4. **Design API-first.** Public behavior should be exposed around FlowPlane domain concepts, not SSISDB tables or another provider’s storage schema.
5. **Own application metadata in PostgreSQL.** FlowPlane’s database stores orchestration and product state; it must not become a clone of SSISDB.
6. **Treat provider systems as external authorities for provider-native facts.** FlowPlane may translate or cache those facts without pretending to own their source representation.
7. **Keep APIs stateless where practical and durable state explicit.** Scheduling and execution recovery cannot rely only on in-memory timers or process lifetime.
8. **Model provider capabilities explicitly.** The platform should discover what a provider or agent can do instead of assuming all providers support the same operations.
9. **Keep the frontend independent of provider implementations.** Provider-specific experiences may extend the UI without redefining the core UI around SSIS.
10. **Do not make React Flow the backend domain model by default.** It is primarily an editor/view technology unless an explicit decision establishes otherwise.
11. **Design for observability and security from the start.** Execution visibility, health, auditability, least privilege, and protected secret handling are product concerns, not afterthoughts.
12. **Use OS-specific execution agents where required.** Container-friendly control-plane deployment must not force platform-specific runtimes into control-plane containers.

### Assumptions

- “Provider independence” means stable core concepts with provider-specific extensions, not a lowest-common-denominator interface that erases useful provider behavior.
- “Stateless API where practical” still permits durable workers and schedulers as separate logical responsibilities, even if an early deployment packages some responsibilities together.

### Recommendations

- Evaluate every domain entity, API field, and UI concept with a simple test: could a non-SSIS provider implement this without pretending to be SSIS?
- Treat durability, idempotency, and reconciliation as acceptance criteria for execution orchestration rather than optional scaling enhancements.

## 3. Control Plane vs Execution Plane understanding

### Existing decisions

The **Control Plane** is the authoritative product and orchestration layer. It is expected to own:

- workflows and immutable workflow versions;
- schedules, triggers, dependencies, queues, priorities, retries, timeouts, and concurrency policy;
- execution requests and provider-independent orchestration state;
- agents/workers, their registration, capabilities, health, and assignment metadata;
- environments, deployments, connections, secret references, and provider configuration;
- users, roles, permissions, audit history, monitoring, and alerting;
- APIs, the web application, the scheduler, the dispatcher, and background processing;
- FlowPlane metadata persisted in PostgreSQL.

The **Execution Plane** contains the runtimes and infrastructure that perform work. It may include Windows agents, Linux agents, remote SSIS/SQL Server environments, or future provider-specific workers. It owns provider-native execution behavior and facts such as:

- DTSX and ISPAC semantics;
- SSIS runtime behavior, parameters, events, messages, and data-flow statistics;
- provider-native operation identifiers, diagnostics, and cancellation behavior;
- execution constraints imposed by the provider platform or operating system.

The boundary is an adapter boundary: the control plane issues durable, idempotent execution intent; an appropriate agent/provider translates that intent into native commands; native results are translated back into provider-independent status plus optional provider-specific detail.

### Assumptions

- FlowPlane’s execution record and the provider’s native execution record are related but distinct. Both identifiers must be retained for correlation and recovery.
- FlowPlane owns the requested orchestration lifecycle, while the provider remains authoritative for what its runtime actually did. Disagreement requires reconciliation rather than blind overwrite.
- An agent is a likely execution-plane boundary for network isolation and OS-specific behavior, but the brief has not finalized whether every provider operation must go through an agent.

### Recommendations

- Specify authority for every execution field and transition before implementing the state machine.
- Define recovery around persisted intent, leases, heartbeats, idempotent commands, and reconciliation. Exact mechanisms remain an open decision.

## 4. Provider architecture understanding

### Existing decisions

SSIS is a provider behind a clean boundary, not a special case embedded throughout the FlowPlane core. The shared model should contain provider-independent concepts such as workflow identity/version, execution request, attempt, lifecycle status, scheduling policy, agent assignment, timestamps, and audit information.

Provider-specific concepts should remain in provider adapters and typed provider extensions. For SSIS, these include packages/projects, catalog operations, SSIS parameters, events, messages, and data-flow statistics. Adapters translate between the FlowPlane contract and native provider APIs while preserving native identifiers and diagnostics.

Provider and agent capabilities should express supported behavior. Candidate capabilities in the brief include validation, deployment, cancellation, parameters, environments, streaming logs, statistics, remote execution, and Windows authentication. Capabilities are preferable to assuming that every provider implements one large uniform interface.

### Assumptions

- A small common lifecycle contract will coexist with optional capability-specific contracts or handlers.
- Capability declarations must include enough detail for compatibility checks; simple booleans may be insufficient when versions, modes, limits, or configuration affect support.
- Provider-specific data can be exposed as an extension without becoming required in the generic execution model.

### Recommendations

- Begin with the smallest contract required by the selected MVP journey and add capability contracts only when a real provider behavior requires them.
- Add provider contract tests once the provider boundary is specified, so future implementations demonstrate common lifecycle behavior without copying SSIS semantics.

### Open questions

- What is the precise provider contract, and is its primary abstraction an execution provider, workflow provider, integration provider, or a composition of smaller roles?
- Which capabilities belong to providers, which belong to agents, and which require a combination of both?
- How are capability versions, limits, and configuration-dependent support represented and negotiated?
- Which provider-specific payloads are typed, versioned extensions versus opaque data?
- Can providers run in-process, out-of-process, or both, and what compatibility/isolation contract applies?

## 5. Important constraints

### Existing decisions

- The control plane must run on Linux and Windows and be container-friendly.
- Execution environments may be platform-specific; SSIS must not be assumed to run in the FlowPlane container.
- PostgreSQL is intended for FlowPlane metadata, not as a replica of provider databases.
- SQL Server Agent must not be the central cross-environment scheduler.
- Durable scheduling and execution state are required; a simple in-process timer is not considered production-grade architecture.
- The core domain, REST API, and frontend must be organized around FlowPlane concepts rather than SSISDB schema.
- Raw database passwords must not be stored in PostgreSQL without an explicit secure design. Secrets should be represented by references and handled with least privilege.
- The architecture must handle duplicate commands, restarts, stale agents, network partitions, orphaned executions, cancellation, and retries without assuming exactly-once delivery.
- Workflow versions, deployments, promotions, and rollback need an immutable/auditable model across Development, Test, UAT, and Production-like environments.
- Infrastructure dependencies must justify their MVP value. A message broker, Kafka, Redis, Temporal, Hangfire, Quartz.NET, gRPC, or WebSockets are candidates to evaluate, not automatic dependencies.
- Current provider/platform behavior must be verified against authoritative documentation; claims in the brief are research inputs, not verified facts in this document.

### Assumptions

- The preferred React/TypeScript, ASP.NET Core/C#, PostgreSQL, Entity Framework Core, and React Flow choices are current candidates, not all irrevocable decisions, because the brief explicitly asks that they be challenged.
- Kubernetes, distributed scheduling, leader election, event triggers, multi-tenancy, and a plugin ecosystem are evolution concerns unless the MVP definition promotes them.
- Provider-native logs and statistics may be queried, cached, copied, or streamed depending on latency, retention, and availability requirements; no universal ingestion policy has been approved.

### Recommendations

- Maintain a decision ledger that distinguishes “chosen,” “preferred,” “under evaluation,” and “deferred.”
- Verify SSIS platform limitations, deployment APIs, execution APIs, catalog views, and container support against current Microsoft documentation before fixing the SSIS adapter boundary.

## 6. MVP considerations

### Existing decisions

The MVP must be intentionally narrow, useful, and evolvable. The brief proposes a phased sequence—minimal platform, SSIS integration, scheduling/monitoring, designers, distributed agents, and provider ecosystem—but presents that sequence for evaluation rather than as a finalized delivery plan.

The architecture should leave room for durable scheduling, agents, richer monitoring, designers, and additional providers without implementing all of them on day one.

### Assumptions

A coherent MVP probably needs one end-to-end slice containing:

- persisted FlowPlane metadata and an explicit workflow/execution identity;
- one provider integration path;
- manual execution before advanced triggers and DAG scheduling;
- a minimal provider-independent lifecycle and status view;
- correlation to native provider execution details;
- basic authentication/authorization, secret-reference handling, auditing, health, and structured diagnostics appropriate to the deployment;
- failure recovery sufficient to avoid silently losing or duplicating execution intent.

This is a scope hypothesis, not an approved roadmap.

### Recommendations

- Choose one deployment topology and one SSIS execution mode for the first vertical slice.
- Prefer manual execution and basic historical monitoring before cron, DAGs, event triggers, advanced retries, and distributed scheduler high availability.
- Defer the generalized visual workflow designer, SSIS package authoring, multi-provider marketplace, multi-tenancy, complex promotion approvals, and advanced analytics until the execution boundary is proven.
- Do not add an infrastructure dependency until a documented MVP requirement cannot be met cleanly with the preferred baseline stack.
- Define explicit MVP non-goals alongside acceptance criteria to prevent the master brief from becoming a single-release backlog.

## 7. Major decisions already made

The following are the strongest decisions expressed by the current source:

| Area | Existing decision |
| --- | --- |
| Product | Open-source centralized workflow control plane for heterogeneous execution environments. |
| Initial provider | SSIS is first, but it is not the platform foundation. |
| System boundary | Control-plane orchestration is separate from provider/agent execution. |
| Portability | Control plane runs on Linux and Windows and is container-friendly; execution may remain OS-specific. |
| Data ownership | PostgreSQL owns FlowPlane application/orchestration metadata; provider stores remain authoritative for provider-native facts. |
| Scheduling | FlowPlane, not SQL Server Agent, is intended to become the central scheduler. |
| Extensibility | Provider-specific behavior is isolated behind adapters and capabilities. |
| API/UI | API and UI use FlowPlane domain concepts, not SSISDB tables or frontend-library serialization as the backend domain. |
| Reliability | Scheduling and execution intent are durable and designed for retries, duplicates, crashes, and recovery. |
| Security | Secrets and remote credentials require an explicit protected design; least privilege and auditability are baseline concerns. |
| Delivery | Start with a pragmatic MVP and evolve; do not build the entire envisioned platform first. |

The preferred technology list and example entities, endpoints, interfaces, execution states, transports, deployment layouts, and roadmap phases are not recorded here as final decisions.

## 8. Open architectural questions

### Product and MVP boundary

- What exact user journey defines the first useful release?
- Is SSIS execution part of Phase 1 or a separate Phase 2, and which SSIS deployment/execution mode is first?
- Which features are explicit MVP non-goals?

### Component and deployment boundaries

- Which control-plane responsibilities are separate deployable processes versus logical modules in one initial deployment?
- What is the initial supported topology for local development, Linux production, Windows production, and hybrid environments?
- Is an agent mandatory for the first SSIS integration, and where may providers execute?

### Scheduler and dispatch

- Which durable scheduling mechanism is selected for the MVP?
- What are the precise rules for time zones, daylight-saving transitions, misfires, backoff, priority, concurrency, and dependencies?
- When are scheduler high availability, leader election, leases, and distributed dispatch required?
- What idempotency keys and uniqueness rules prevent duplicate execution requests?

### Agent communication and trust

- Do agents poll over REST/long polling, maintain gRPC or WebSocket connections, or consume brokered commands?
- How do registration, mutual authentication, certificate/token rotation, revocation, and authorization work?
- How are heartbeats, capabilities, resource availability, draining, upgrades, and stale-agent detection modeled?

### Execution lifecycle and authority

- What is the approved state machine, including valid transitions and terminal-state reconciliation?
- How are execution attempts separated from the parent execution and retry policy?
- Who may transition each state, and how are conflicting control-plane, agent, and provider observations reconciled?
- How are cancellation races, timeouts, agent loss, orphaned native executions, and late results handled?

### Provider model

- What common contract is truly provider-independent?
- How are optional capabilities discovered, versioned, and validated?
- How are provider artifacts, configuration schemas, native identifiers, errors, logs, and statistics represented without abstraction leakage?
- What is the provider/plugin loading, isolation, compatibility, and semantic-versioning model?

### Data ownership, monitoring, and retention

- For each SSIS datum, is SSISDB authoritative, queried live, cached, copied for retention/search, or streamed?
- What consistency and freshness guarantees does the UI communicate?
- What happens when provider history expires or the provider is unreachable?
- Which operational data belongs in PostgreSQL versus an external observability stack, and what are retention/redaction rules?

### Workflow and designer model

- What is the canonical backend workflow schema and its evolution/versioning strategy?
- How does React Flow editor state map to the canonical domain without becoming that domain accidentally?
- Are orchestration workflow and provider-native data-flow editors separate products sharing graph infrastructure, or one generalized editor?
- For SSIS authoring, does FlowPlane edit DTSX, compile an intermediate model, preserve dual representations, or defer package authoring?

### Security, tenancy, and environments

- What authentication mechanism and RBAC scope are required first?
- Is Organization/Workspace multi-tenancy an MVP concept, a future concept, or unnecessary?
- Which secrets system is used, and what crosses the control-plane/agent boundary?
- How are immutable versions promoted through environments, approved, deployed, rolled back, and audited?

### API and compatibility

- What resource model, error format, pagination, filtering, optimistic concurrency, idempotency, and versioning conventions are adopted?
- Which APIs are public stability commitments versus internal implementation details?
- How are provider-specific extensions exposed without fragmenting clients?

### Technology choices

- Which listed preferred technologies are approved versus still under evaluation?
- Does the MVP require a scheduler library, queue, broker, cache, real-time transport, or workflow engine beyond the baseline stack?
- What evidence or threshold justifies introducing each additional operational dependency?

## 9. Recommended decision sequence

These are process recommendations, not architecture changes:

1. Resolve the canonical master-document path and version it as a stable project reference.
2. Approve the MVP user journey, topology, non-goals, and first SSIS execution mode.
3. Record control-plane/provider data authority and the minimum execution state machine.
4. Define the smallest provider/capability contract required by that journey.
5. Decide agent placement, communication, and trust for the chosen topology.
6. Select the simplest durable scheduler/dispatcher design that meets the approved MVP failure model.
7. Define security, secrets, audit, and observability baselines before handling production credentials or workloads.
8. Defer designer and package-authoring architecture decisions until the execution and provider boundaries have been validated, unless visual authoring is made an explicit MVP requirement.

This sequence preserves the stated architecture while turning unresolved parts of the brief into reviewable decisions rather than accidental implementation choices.
