# FlowPlane Documentation Plan

## 1. Status and scope

This document proposes the documentation architecture for the FlowPlane repository. It is a planning and AI/development guidance document; it is not architectural authority and does not approve any unresolved architectural choice.

The authoritative source for overall product and architectural intent is [`docs/product/FlowPlane-Master-Architecture-and-Product-Design.md`](../docs/product/FlowPlane-Master-Architecture-and-Product-Design.md). The existing [`project-understanding.md`](project-understanding.md) is a derived understanding document and does not replace the master architecture document.

This plan proposes files and creation priorities only. It does not create the proposed public documentation, ADRs, domain specifications, guides, references, or roadmaps.

### Decision-status vocabulary

Documentation created from this plan must distinguish:

- **Existing decision**: product direction, architectural intent, principle, or constraint already established by the master architecture document or an accepted ADR.
- **Recommendation**: a proposed approach that is not authoritative until reviewed and accepted through the appropriate decision process.
- **Assumption**: a working interpretation that requires validation.
- **Open question**: an unresolved choice that must not be described as an accepted design.

### Delivery-timing vocabulary

- **Existing**: the file already exists.
- **Needed soon**: foundational documentation needed to support near-term architecture and MVP work.
- **Needed later**: useful after the foundational decisions and implementation boundaries are clearer.
- **Conditional**: create only if the associated feature, interface, or operational capability is implemented.

Delivery timing indicates documentation priority, not product roadmap approval.

## 2. Authority model

1. The master architecture document defines overall product and architectural intent.
2. Accepted ADRs are authoritative for the specific architectural decisions they record.
3. When an accepted decision changes, a new ADR supersedes the previous ADR rather than silently rewriting its history.
4. Architecture documents describe the current architecture and must remain consistent with accepted ADRs.
5. Guides and reference documents explain usage and implementation without introducing architectural decisions.
6. `.ai` documents are AI/development guidance and are not architectural authority.

### Applying the authority model

- The master architecture document supplies the broad vision, boundaries, principles, constraints, and decision agenda.
- An accepted ADR governs the specific decision within its stated scope. Superseded ADRs remain available as historical records and link to their successors.
- Architecture and domain documents describe the currently accepted design. They link to the governing ADRs rather than reproducing decision history.
- Guides explain procedures. Reference documents describe exact interfaces, configuration, states, or schemas that already exist.
- Roadmap documents describe sequencing and priorities, not architectural truth.
- `.ai/project-understanding.md` and this plan may summarize or classify information for development assistance, but public and architectural documents must not cite them as authority.

## 3. Proposed documentation structure

The following tree is the target documentation structure. The files shown are proposals; only the master architecture document currently exists under `docs/`.

```text
docs/
├── README.md                                                [needed soon]
├── product/
│   ├── FlowPlane-Master-Architecture-and-Product-Design.md  [existing]
│   ├── README.md                                            [needed soon]
│   ├── vision-and-scope.md                                 [needed soon]
│   ├── mvp-scope.md                                        [needed soon]
│   └── use-cases.md                                        [needed later]
├── architecture/
│   ├── README.md                                            [needed soon]
│   ├── overview.md                                          [needed soon]
│   ├── control-plane.md                                     [needed soon]
│   ├── execution-plane.md                                   [needed soon]
│   ├── provider-architecture.md                             [needed soon]
│   ├── ssis-provider.md                                     [needed soon]
│   ├── metadata-and-data-ownership.md                       [needed soon]
│   ├── execution-lifecycle.md                               [needed soon]
│   ├── scheduling-and-dispatch.md                           [needed later]
│   ├── monitoring-and-observability.md                      [needed later]
│   ├── security.md                                          [needed soon]
│   ├── deployment.md                                        [needed later]
│   ├── agents.md                                            [conditional]
│   ├── workflow-designer.md                                 [conditional]
│   └── ssis-package-authoring.md                            [conditional]
├── domain/
│   ├── README.md                                            [needed soon]
│   ├── glossary.md                                          [needed soon]
│   ├── core-concepts.md                                     [needed soon]
│   ├── workflow-model.md                                    [needed soon]
│   ├── execution-model.md                                   [needed soon]
│   ├── provider-capability-model.md                         [needed soon]
│   ├── scheduling-model.md                                  [needed later]
│   ├── environments-and-deployments.md                      [needed later]
│   ├── connections-artifacts-and-secret-references.md       [needed later]
│   └── identity-permissions-and-audit.md                    [needed later]
├── adr/
│   ├── README.md                                            [needed soon]
│   ├── template.md                                          [needed soon]
│   └── NNNN-<decision-title>.md                              [conditional]
├── guides/
│   ├── README.md                                            [needed later]
│   ├── development-setup.md                                 [needed soon]
│   ├── getting-started.md                                   [needed later]
│   ├── running-and-monitoring-workflows.md                  [conditional]
│   ├── configuring-ssis-integration.md                      [conditional]
│   ├── deploying-flowplane.md                               [conditional]
│   ├── operating-an-agent.md                                [conditional]
│   └── adding-a-provider.md                                 [conditional]
├── reference/
│   ├── README.md                                            [needed later]
│   ├── configuration.md                                     [needed later]
│   ├── execution-states.md                                  [needed soon]
│   ├── api.md                                               [conditional]
│   ├── error-model.md                                       [conditional]
│   ├── provider-contract.md                                 [conditional]
│   ├── capability-catalog.md                                [conditional]
│   └── security-configuration.md                            [conditional]
└── roadmap/
    ├── README.md                                            [needed soon]
    ├── phases.md                                            [needed later]
    └── future-capabilities.md                               [needed later]

.ai/
├── project-understanding.md                                 [existing]
└── documentation-plan.md                                    [this document]
```

The proposed tree does not authorize creating any listed file. Each file should be created only when its prerequisite decisions or implemented behavior are sufficiently stable to document accurately.

## 4. Purpose of each documentation area and file

### 4.1 Repository and documentation entry points

| File | Timing | Purpose and boundary |
| --- | --- | --- |
| `README.md` | Existing; update needed soon | Public entry point: concise product description, project status, first links, and documentation navigation. It summarizes rather than restating the master architecture document. |
| `docs/README.md` | Needed soon | Top-level public documentation index: authority model, documentation map, audience-based navigation, and links to each documentation area. It remains navigational and does not restate child documents. |
| `CONTRIBUTING.md` | Existing; content needed soon | Contributor entry point: contribution workflow, review expectations, tests, documentation expectations, and links to detailed development guidance. It must not define architecture. |
| `CODE_OF_CONDUCT.md` | Existing | Public community participation expectations. It is independent of product and architecture documentation. |
| `LICENSE` | Existing | Project license. Other documents link to it instead of paraphrasing legal terms. |

### 4.2 `docs/product/`

| File | Timing | Purpose and boundary |
| --- | --- | --- |
| `FlowPlane-Master-Architecture-and-Product-Design.md` | Existing | Authoritative source for overall product and architectural intent. It remains the primary reference and decision agenda. |
| `README.md` | Needed soon | Index for product documentation, including the authority relationship between the master document, accepted ADRs, and derived documents. |
| `vision-and-scope.md` | Needed soon | Concise public explanation of the product problem, intended users, high-level scope, and product boundaries. It derives from and links to the master document; it does not introduce new scope. |
| `mvp-scope.md` | Needed soon | Records the approved MVP objective, inclusions, exclusions, and acceptance criteria. Until MVP scope is accepted, this file must label proposals and open questions rather than imply approved scope. |
| `use-cases.md` | Needed later | Describes validated user journeys and scenarios that motivate product behavior. It must not redefine domain entities or settle architecture. |

### 4.3 `docs/architecture/`

Architecture documents describe the current accepted architecture. They must link to governing ADRs and must not preserve obsolete designs as if they were current.

| File | Timing | Purpose and boundary |
| --- | --- | --- |
| `README.md` | Needed soon | Architecture index, reading order, status conventions, and links to the master document and accepted ADRs. |
| `overview.md` | Needed soon | Current system context, major boundaries, component responsibilities, and the relationship among control plane, execution plane, agents, and providers. |
| `control-plane.md` | Needed soon | Current control-plane responsibilities, boundaries, persistence ownership, and component interactions. |
| `execution-plane.md` | Needed soon | Current execution-plane responsibilities, platform-specific runtime boundaries, and interaction with the control plane. |
| `provider-architecture.md` | Needed soon | Provider-independent boundary, adapter responsibilities, extension rules, and accepted capability approach. Unresolved contract details remain labeled as open questions until decided. |
| `ssis-provider.md` | Needed soon | SSIS-specific adapter boundary, authoritative provider data, supported operations, and integration constraints, based only on verified behavior and accepted decisions. |
| `metadata-and-data-ownership.md` | Needed soon | Ownership and authority boundaries among PostgreSQL, provider systems such as SSISDB, caches, and derived monitoring data. |
| `execution-lifecycle.md` | Needed soon | Accepted execution state ownership, transitions, attempts, retries, recovery, cancellation, reconciliation, and failure handling. It must not invent a state machine before one is accepted. |
| `scheduling-and-dispatch.md` | Needed later | Accepted scheduling, queueing, dispatch, concurrency, idempotency, recovery, and high-availability behavior. |
| `monitoring-and-observability.md` | Needed later | Current monitoring, logging, metrics, tracing, health, retention, and provider-native diagnostic boundaries. |
| `security.md` | Needed soon | Current trust boundaries, identity model, authorization, agent authentication, secret handling, audit behavior, and least-privilege requirements. Open mechanisms remain explicitly unresolved. |
| `deployment.md` | Needed later | Supported deployment topologies and component placement after those topologies are accepted and supported. |
| `agents.md` | Conditional | Create if the agent feature is implemented. Describe registration, communication, capabilities, heartbeats, assignment, trust, recovery, and lifecycle. |
| `workflow-designer.md` | Conditional | Create if a visual workflow or data-flow designer is implemented. Describe the accepted boundary between editor representation and canonical backend models. |
| `ssis-package-authoring.md` | Conditional | Create if FlowPlane implements SSIS package authoring. Describe the accepted DTSX/intermediate-model strategy and preservation rules. |

### 4.4 `docs/domain/`

Domain documents define shared language, entities, relationships, and invariants. They do not describe deployment topology or component internals.

| File | Timing | Purpose and boundary |
| --- | --- | --- |
| `README.md` | Needed soon | Domain documentation index and rules for distinguishing provider-independent concepts from provider-specific extensions. |
| `glossary.md` | Needed soon | Single canonical glossary for FlowPlane terms and acronyms. Other documents link to definitions here. |
| `core-concepts.md` | Needed soon | Stable definitions and relationships for the smallest accepted core domain, without blindly including every entity proposed by the master document. |
| `workflow-model.md` | Needed soon | Accepted workflow, workflow-version, node, edge, and serialization concepts. Undecided canonical-model questions remain explicit until resolved. |
| `execution-model.md` | Needed soon | Accepted definitions and invariants for execution, attempt, event, log correlation, status, and provider-native execution identity. |
| `provider-capability-model.md` | Needed soon | Accepted definitions for providers, capabilities, capability negotiation, and provider-specific extensions. It must not assume that the proposed capability model is already finalized. |
| `scheduling-model.md` | Needed later | Accepted domain concepts for schedules, triggers, dependencies, priorities, concurrency, retries, and timeouts. |
| `environments-and-deployments.md` | Needed later | Accepted definitions for environments, deployments, promotion, approvals, versions, and rollback. |
| `connections-artifacts-and-secret-references.md` | Needed later | Accepted provider-independent definitions and relationships for connections, artifacts, provider configuration, and secret references. It must not document raw secret values. |
| `identity-permissions-and-audit.md` | Needed later | Accepted domain concepts for users, roles, permissions, service identities, and audit events. |

### 4.5 `docs/adr/`

| File | Timing | Purpose and boundary |
| --- | --- | --- |
| `README.md` | Needed soon | Explains ADR purpose, lifecycle, status values, numbering, review, acceptance, and supersession. |
| `template.md` | Needed soon | Provides a consistent structure for context, decision, alternatives, consequences, status, and supersession links. It must not prescribe an architectural outcome. |
| `NNNN-<decision-title>.md` | Conditional | Create one immutable decision record per accepted architectural decision. Reversal or material change creates a superseding ADR. |

No ADR file should be created merely because a topic appears in the master document. A topic becomes an ADR only when its decision and rationale have been reviewed and accepted. A retrospective ADR is appropriate only for an architecturally significant existing decision whose rationale and consequences are valuable to preserve.

### 4.6 `docs/guides/`

Guides are task-oriented public instructions. They use accepted architecture and implemented behavior; they do not create either.

| File | Timing | Purpose and boundary |
| --- | --- | --- |
| `README.md` | Needed later | Guide index organized by user goal. |
| `development-setup.md` | Needed soon | Reproducible local contributor setup based on the repository as implemented. Root `CONTRIBUTING.md` links here instead of duplicating steps. |
| `getting-started.md` | Needed later | First successful user journey once a runnable release exists. |
| `running-and-monitoring-workflows.md` | Conditional | Create when workflow execution and monitoring are available to users. |
| `configuring-ssis-integration.md` | Conditional | Create when the supported SSIS integration path exists. |
| `deploying-flowplane.md` | Conditional | Create when supported production deployment paths exist. |
| `operating-an-agent.md` | Conditional | Create when agents are implemented and supported. |
| `adding-a-provider.md` | Conditional | Create when the provider extension contract is stable enough for external contributors. |

### 4.7 `docs/reference/`

Reference documents are exact, descriptive, and implementation-aligned. They do not contain decision rationale or roadmap promises.

| File | Timing | Purpose and boundary |
| --- | --- | --- |
| `README.md` | Needed later | Reference index and compatibility/versioning notes. |
| `configuration.md` | Needed later | Supported configuration keys, sources, defaults, validation, and security notes once configuration exists. |
| `execution-states.md` | Needed soon | Exact accepted execution states, transition rules, ownership, terminal behavior, and correlation fields. Publish only after the lifecycle decision is accepted. |
| `api.md` | Conditional | Exact public API resources and conventions once the API exists and its stability expectations are defined. |
| `error-model.md` | Conditional | Exact public error representation and codes once an error contract exists. |
| `provider-contract.md` | Conditional | Exact provider interfaces, lifecycle obligations, versioning, and compatibility rules once implemented and designated for external use. |
| `capability-catalog.md` | Conditional | Exact supported capabilities and semantics once capability contracts exist. |
| `security-configuration.md` | Conditional | Exact identity, credential, certificate, token, secret-reference, and authorization configuration once supported. It must never contain real credentials. |

### 4.8 `docs/roadmap/`

Roadmap documents describe intended sequencing. They must distinguish committed work from possibilities and must not be treated as architecture authority.

| File | Timing | Purpose and boundary |
| --- | --- | --- |
| `README.md` | Needed soon | Roadmap status vocabulary, navigation, and links. |
| `phases.md` | Needed later | Approved delivery sequencing and phases after the master document's proposed phases are reviewed. It links to `docs/product/mvp-scope.md` for MVP inclusions, exclusions, and acceptance criteria. Proposed phases remain recommendations until accepted. |
| `future-capabilities.md` | Needed later | Non-committed future possibilities, explicitly separated from accepted scope and delivery commitments. |

### 4.9 `.ai/`

The `.ai` directory remains intentionally limited for now.

| File | Timing | Purpose and boundary |
| --- | --- | --- |
| `project-understanding.md` | Existing | Derived understanding of product intent, decisions, assumptions, recommendations, and open questions. It is not architecture authority. |
| `documentation-plan.md` | This document | AI/development guidance for documentation organization, authority, ownership, timing, and non-duplication. It does not make architectural decisions. |

Do not recreate `.ai/project-context.md`, `.ai/architecture-rules.md`, or `.ai/development-rules.md` until their need and non-overlapping purpose are separately reviewed.

## 5. Relationship between source and derived documents

```text
Master architecture document
    defines overall intent and the decision agenda
                    |
                    v
Accepted ADRs
    govern specific accepted architectural decisions
                    |
                    v
Architecture and domain documents
    describe the current accepted system and shared model
                    |
          +---------+---------+
          |                   |
          v                   v
       Guides             Reference
  explain user tasks   describes exact behavior

Roadmap documents describe sequencing, not authority.
.ai documents assist development, not architecture governance.
```

When documents conflict:

1. For overall product and architectural intent, consult the master architecture document.
2. For a specific accepted decision, consult the applicable accepted ADR, including any supersession chain.
3. Correct architecture and domain documents to match accepted ADRs.
4. Correct guides and references to match the implemented, accepted design.
5. Never resolve a conflict by treating an `.ai` document as architectural authority.

## 6. ADR topics

### 6.1 Candidate existing decisions for selective retrospective ADRs

Do not automatically create retrospective ADRs for every existing decision or architectural principle. Create one only when the decision is architecturally significant and preserving its rationale and consequences will help future maintainers understand or change the system safely.

The topics below are candidates for evaluation, not a required ADR backlog. Each candidate still requires a significance assessment, review, and acceptance.

| Candidate ADR topic | Existing decision or principle to evaluate |
| --- | --- |
| Control-plane and execution-plane separation | FlowPlane orchestration is separated from provider-specific execution environments. |
| SSIS as a provider | SSIS is the first major provider, not the architectural foundation of the platform. |
| Control-plane platform independence | The control plane runs on Linux and Windows and is container-friendly; execution may remain OS-specific. |
| FlowPlane metadata ownership | PostgreSQL owns FlowPlane application and orchestration metadata rather than replicating provider databases. |
| Provider-native authority | Provider systems remain authoritative for provider-native execution facts and diagnostics. |
| FlowPlane-owned scheduling | FlowPlane, rather than SQL Server Agent, is intended to own centralized orchestration and scheduling. |
| Provider-independent domain and API | Core domain and API contracts are organized around FlowPlane concepts rather than SSISDB tables. |
| Durable orchestration state | Scheduling and execution recovery rely on persisted state rather than only process-local timers. |
| Secure secret references | Credentials require an explicit protected design; raw database passwords are not ordinary PostgreSQL metadata. |
| Pragmatic evolutionary delivery | The project begins with a small MVP and avoids building the full future platform before validating the core. |

### 6.2 ADRs needed only after open questions are resolved

The following are decision candidates, not current decisions. An ADR should be created only after the project evaluates alternatives and accepts an outcome.

- Exact provider contract decomposition and extension boundary.
- Capability representation, ownership, negotiation, and versioning.
- MVP scheduler and durable dispatch mechanism.
- Scheduler high availability, leader election, leases, and duplicate prevention.
- Agent placement, communication transport, registration, and trust model.
- Execution lifecycle, state-transition ownership, attempts, retries, and reconciliation.
- Provider log, event, statistics, caching, ingestion, and retention boundaries.
- Canonical workflow model and schema-evolution strategy.
- Relationship between React Flow editor state and the canonical workflow model.
- SSIS package authoring strategy, including DTSX and any intermediate representation.
- Authentication, authorization, RBAC, service identity, and secrets mechanism.
- Multi-tenancy or Organization/Workspace scope.
- Deployment topology and initial process boundaries.
- Adoption of any additional scheduler, broker, cache, workflow engine, or real-time transport.

Creating this list does not recommend or select an outcome for any topic.

### 6.3 Feature-triggered ADR topics

These ADRs are only needed if the associated feature enters implementation:

- Visual workflow or data-flow designer architecture.
- Agent protocol and lifecycle.
- External provider/plugin loading and compatibility.
- SSIS package authoring and round-trip preservation.
- Event-based triggers.
- Multi-tenant isolation.
- Production high-availability topology.

## 7. Public and contributor documentation

The following are public project documents:

- Repository-root `README.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, and `LICENSE`.
- `docs/README.md` and every document under `docs/product/`, `docs/architecture/`, `docs/domain/`, `docs/adr/`, `docs/guides/`, `docs/reference/`, and `docs/roadmap/`.

Contributor-specific entry points are:

- `CONTRIBUTING.md` for contribution policy and workflow.
- `docs/guides/development-setup.md` for detailed local setup.
- `docs/architecture/README.md` and `docs/domain/README.md` for technical orientation.
- `docs/adr/README.md` and `docs/adr/template.md` for architectural decision participation.
- `docs/guides/adding-a-provider.md` only after an external provider contract is implemented and supported.

Public documents must be understandable without reading `.ai` files.

## 8. AI/development guidance

For now, AI/development guidance consists only of:

- `.ai/project-understanding.md`
- `.ai/documentation-plan.md`

These files may help an AI assistant or contributor orient itself, classify source material, and avoid accidental architectural changes. They must:

- link to authoritative sources;
- label decisions, recommendations, assumptions, and open questions;
- avoid becoming the only location for public or architectural knowledge;
- avoid restating detailed contracts or procedures owned elsewhere;
- be corrected when they conflict with the master document or accepted ADRs.

## 9. Information that must not be duplicated

| Information | Single owning location | Other documents should |
| --- | --- | --- |
| Overall product and architectural intent | Master architecture document | Summarize briefly and link to the relevant section. |
| Specific decision, rationale, alternatives, and consequences | Applicable accepted ADR | State the current result and link to the ADR. |
| ADR history and supersession | ADR chain | Link to the current and superseded records; never rewrite old ADRs silently. |
| Current component boundaries and interactions | Relevant `docs/architecture/` document | Link instead of maintaining parallel diagrams or descriptions. |
| Domain terms, entity definitions, and invariants | Relevant `docs/domain/` document, with shared terms in `glossary.md` | Reuse the canonical term and link to its definition. |
| Exact API, configuration, state, error, or provider contract | Relevant `docs/reference/` document | Use examples that link back to the reference instead of copying the contract. |
| Task instructions | Relevant `docs/guides/` document | Link to the procedure instead of embedding a second set of steps. |
| MVP scope | `docs/product/mvp-scope.md` after approval | Link from roadmaps and guides; do not redefine inclusions, exclusions, or acceptance criteria. |
| Delivery sequencing and phases | `docs/roadmap/phases.md` | Link to the roadmap; do not present delivery timing as architectural necessity or redefine MVP scope. |
| Contribution workflow | Root `CONTRIBUTING.md` | Link from development guides rather than repeating policy. |
| License terms | Root `LICENSE` | Link to the license rather than paraphrasing legal text. |
| AI orientation and planning notes | `.ai/` documents | Never require public readers to rely on them. |

Additional non-duplication rules:

- Do not split one concept's normative definition across architecture, domain, and reference documents. Choose one owner and use links elsewhere.
- Do not copy the master architecture document into smaller files. Derived documents should be narrower, current, and traceable.
- Do not copy decision rationale into architecture overviews. Keep current-state explanation in architecture documents and historical rationale in ADRs.
- Do not use roadmap files to preserve unresolved architecture proposals. Keep them labeled as open questions in the appropriate product or architecture context.
- Do not duplicate provider-native documentation. Document FlowPlane's integration boundary and link to authoritative provider documentation where appropriate.
- Do not place credentials, real secrets, or environment-specific sensitive values in any documentation.

## 10. Recommended documentation sequence

This sequence is a documentation recommendation, not an implementation roadmap or architectural decision.

### Needed soon

1. Create `docs/README.md` as the top-level public documentation index.
2. Create `docs/product/README.md` to explain product-document authority and navigation.
3. Create `docs/adr/README.md` and `docs/adr/template.md` before recording individual ADRs.
4. Evaluate existing decisions and principles for architectural significance, and create retrospective ADRs only where preserving rationale and consequences is valuable.
5. Create `docs/architecture/README.md` and `docs/architecture/overview.md` from accepted intent and ADRs.
6. Create focused control-plane, execution-plane, provider, metadata-ownership, security, and execution-lifecycle documents as their content becomes accepted.
7. Create the domain glossary and smallest approved core, workflow, execution, and provider-capability models.
8. Define and document approved MVP inclusions, exclusions, and acceptance criteria in `docs/product/mvp-scope.md` without treating the master document's proposed phases as automatically accepted delivery sequencing.
9. Complete root contributor guidance and add the development setup guide using only implemented repository behavior.

### Needed later

- Detailed scheduling, monitoring, deployment, environment, permissions, configuration, and phase documentation.
- Broader user guides and validated use cases.
- Exact references after public contracts and behavior stabilize.

### Conditional

- Agent, designer, package-authoring, external-provider, API, deployment, and operations documentation only when those capabilities are implemented or enter active implementation.

## 11. Assumptions, recommendations, and open documentation questions

### Assumptions

- Repository Markdown remains the primary documentation format unless the project separately decides otherwise.
- Public documentation is reviewed through the same repository contribution process as source changes.
- Proposed filenames may be refined without changing architectural authority, provided their ownership boundaries remain clear.
- Documents marked needed soon are created incrementally as their prerequisites become accepted, not all at once.

### Recommendations

- Add an authority/source note to each derived architecture or domain document.
- Link every current architecture decision to its accepted ADR once the ADR exists.
- Give every ADR an explicit status and supersession relationship.
- Keep indexes short and navigational rather than duplicating child-document summaries.
- Review documentation alongside changes to the behavior or decision it describes.

### Open documentation questions

- Who reviews and accepts ADRs?
- Which ADR statuses and numbering convention will the project adopt?
- Who owns consistency reviews between the master document, ADRs, and current architecture documents?
- When should the master architecture document be revised to reflect accepted decisions while retaining its role as the overall intent reference?
- What release or compatibility policy will govern public reference documentation?
- Whether and when the repository needs a rendered documentation site remains undecided.

These documentation-process questions do not alter FlowPlane's product or software architecture.
