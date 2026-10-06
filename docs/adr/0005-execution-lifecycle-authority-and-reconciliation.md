# ADR 0005: Define Execution Lifecycle, Authority, and Reconciliation

- **Status:** Accepted
- **Supersedes:** None
- **Superseded by:** None

## Context

[ADR-0001](0001-control-plane-execution-plane-separation.md) separates FlowPlane's provider-independent Control Plane from provider-specific runtimes in the Execution Plane. [ADR-0002](0002-separate-integration-families.md) identifies Data-Pipeline Providers as a distinct integration family. [ADR-0003](0003-core-independence-and-progressive-integration.md) requires FlowPlane Core to remain provider-independent and usable without optional enterprise integrations. [ADR-0004](0004-capability-based-data-pipeline-provider-architecture.md) requires every Data-Pipeline Provider to support execution, native execution correlation, and enough basic observation for FlowPlane to track and reconcile an execution.

FlowPlane must turn durable orchestration intent into provider-native execution without treating the two as the same state. External providers have their own runtime identities, state machines, failure modes, and authority. Requests and responses can be lost, FlowPlane processes can restart, a provider can become unreachable while work continues, and cancellation or timeout policy can race with native completion.

The [MVP scope](../product/mvp-scope.md) requires manual and scheduled SSIS execution, cancellation, execution history, native execution correlation, and monitoring. Those capabilities require a provider-independent lifecycle that remains truthful when execution crosses a process and network boundary. The [data-pipeline concepts research](../domain/data-pipeline-concepts.md) identifies `Run`, `RunAttempt`, and `NativeRunReference` as candidate concepts and distinguishes FlowPlane-owned runtime identity from provider-native runtime observations.

This ADR defines provider-independent execution lifecycle principles. It does not define database tables, implementation-language types, REST resources, scheduler or queue implementations, agent architecture, transport protocols, container topology, or detailed SSIS mechanics.

## Decision

The proposed decision is that FlowPlane separates logical execution intent from concrete attempts to initiate and observe provider-native execution.

The execution model has conceptually distinct responsibilities:

- `Run`;
- `RunAttempt`;
- `NativeRunReference`;
- provider-native execution observation;
- orchestration policy and intent;
- reconciliation.

This ADR establishes the conceptual responsibilities and lifecycle semantics below. Exact entity definitions, classes, interfaces, identifiers, and persistence schemas remain technical-design work unless explicitly established here.

### PostgreSQL authority for the MVP

For Minimal FlowPlane and the MVP, PostgreSQL is the authoritative durable store for FlowPlane-owned orchestration state.

FlowPlane-owned runtime correctness must not depend on:

- process memory;
- container-local memory;
- container-local filesystem state;
- an in-memory timer;
- a particular FlowPlane process remaining alive.

PostgreSQL must durably hold enough FlowPlane-owned state to recover incomplete execution work after a FlowPlane process or container restarts. Candidate information includes:

- Run identity and lifecycle;
- RunAttempt identity and lifecycle;
- NativeRunReference;
- execution intent and context;
- cancellation intent;
- timeout and retry policy;
- the latest provider observation or a reference to it;
- reconciliation state;
- schedule-occurrence identity.

This list does not define tables or columns. A persisted provider observation remains an observation of provider-authoritative facts; storing it in PostgreSQL does not transfer authority for the native runtime to FlowPlane.

Kafka is not required for the MVP and is not a co-authoritative source of orchestration state. Future event-streaming or Kafka-based architecture may be evaluated separately, but MVP correctness must not depend on Kafka.

### Run and RunAttempt separation

A **Run** represents one logical FlowPlane execution intent.

A **RunAttempt** represents one FlowPlane attempt to fulfill that Run by initiating a top-level native execution through a Data-Pipeline Provider. A Run may have multiple RunAttempts.

#### FlowPlane-managed retry

A FlowPlane-managed automatic retry creates a new RunAttempt under the same Run.

```text
Run #100
|-- Attempt 1 -> provider execution A -> Failed
`-- Attempt 2 -> provider execution B -> Succeeded
```

The Run may therefore succeed even if earlier attempts failed.

#### Manual rerun

A user-triggered **Run Again** action after a previous logical Run creates a new Run. The new Run may retain an audit or reference relationship to the previous Run, but it is not another attempt of the completed Run.

#### Provider-internal retry

Retries performed internally by a provider remain within the same FlowPlane RunAttempt when FlowPlane did not initiate another top-level provider execution. Examples include Airflow task retries within one DAG Run and other provider-managed retries internal to one native execution.

FlowPlane must not automatically map internal provider retries to FlowPlane RunAttempts.

### Scheduling relationship

`Scheduled` is not a Run lifecycle state. A schedule or trigger occurrence creates a Run.

```text
Schedule occurrence
        |
        v
Run
phase = Requested
```

A scheduled occurrence must have a stable logical identity so duplicate scheduler processing does not create multiple logical Runs for the same occurrence. A candidate conceptual identity is:

```text
(schedule identity, scheduled occurrence time)
```

This ADR does not define the database uniqueness, transaction, lease, or locking mechanism used to enforce that identity.

### Run lifecycle

FlowPlane uses a deliberately small provider-independent Run lifecycle:

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

Run **phase** describes orchestration progress. Run **outcome** is separate from phase.

Possible terminal Run outcomes are:

- `Succeeded`;
- `Failed`;
- `Cancelled`;
- `TimedOut`.

During non-terminal phases, the outcome is absent. These are conceptual lifecycle values, not database enums, API strings, or serialization choices.

A Terminal Run must not become Active again. A later manual rerun creates a new Run.

Cancellation before provider execution may allow either of these transitions when FlowPlane can prove that no native execution was created:

```text
Requested -> Terminal(Cancelled)
Queued    -> Terminal(Cancelled)
```

A Terminal Run represents a final FlowPlane orchestration outcome for the logical execution intent. It does not necessarily mean that every provider-native execution associated with the Run has terminated.

Provider observation and reconciliation may continue after the Run becomes Terminal when a native execution is still active, ambiguous, unreachable, or requires cleanup.

The primary example is a timed-out Run whose native execution has not terminated:

```text
Run:
    phase = Terminal
    outcome = TimedOut

RunAttempt:
    state = Running

Provider-native observation:
    Running

Reconciliation:
    still required
```

FlowPlane must continue preserving provider-native facts after Run terminality. Reconciliation completion is not the same as Run terminality, and a later provider-native result does not automatically reopen a Terminal Run. Exact late-result policy and retention or cleanup behavior remain technical-design decisions.

### RunAttempt lifecycle

FlowPlane uses this conceptual RunAttempt lifecycle:

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

#### Created

The attempt exists durably in FlowPlane, but provider interaction that may create native execution state has not started.

#### Dispatching

FlowPlane has begun provider interaction that may create native execution state, but it does not yet have sufficient durable evidence of the native execution identity or outcome.

Dispatching is an ambiguous distributed-systems boundary. A failed request or lost response during Dispatching must not automatically be treated as proof that no provider-native execution was created.

#### Submitted

FlowPlane has a durable NativeRunReference or equivalent evidence that identifies the provider-native execution. Submitted does not require FlowPlane to have observed the native execution actively running.

#### Running

FlowPlane has observed provider-native evidence that the execution is active.

#### Terminal

The attempt has a settled provider-level result. Possible provider-oriented attempt outcomes include:

- `Succeeded`;
- `Failed`;
- `Cancelled`.

`TimedOut` is not established as a universal provider-native RunAttempt result. A provider may complete so quickly that FlowPlane observes `Submitted -> Terminal` without observing Running.

The exact transition table, persistence representation, and command handlers remain technical-design work.

### Execution authority

FlowPlane owns orchestration intent and orchestration policy. The Data-Pipeline Provider owns provider-native execution facts.

Examples of FlowPlane authority include:

- Run identity;
- the reason or trigger for the Run;
- scheduling intent;
- requested parameters and context;
- retry policy;
- timeout policy;
- cancellation intent;
- RunAttempt identity;
- provider-instance selection;
- the final FlowPlane orchestration outcome.

Examples of provider-native authority include:

- native run or execution identity;
- native execution status;
- native start and end timestamps;
- native errors and diagnostics;
- native inner-unit, task, or component state;
- native provider statistics.

FlowPlane must preserve provider-native observations rather than overwrite or reinterpret them as though FlowPlane owned the native runtime.

### NativeRunReference and provider observation

FlowPlane must preserve correlation between each relevant RunAttempt and its provider-native runtime identity.

A provider-native observation is conceptually separate from FlowPlane Run and RunAttempt state. Candidate observation information may include:

- native status;
- native status code;
- observed-at timestamp;
- native start time;
- native end time;
- provider-native result;
- provider-specific extension details.

This ADR does not finalize a native-observation schema. Native provider status and normalized FlowPlane state must remain distinguishable.

### Basic observation and freshness

Provider connectivity failure does not imply execution failure. If the most recent provider observation says that a native execution is Running and the provider becomes unreachable, FlowPlane must preserve the last-known provider fact rather than convert it to Failed merely because observation is unavailable.

Observation freshness is therefore conceptually separate from execution lifecycle. Possible observation-health conditions include:

- Fresh;
- Stale;
- Unavailable.

These names are illustrative and do not establish a persistence or API schema.

### Cancellation semantics

Cancellation is durable orchestration intent before it is a confirmed execution outcome.

For a Run with no provider-native execution, FlowPlane may settle the Run as Cancelled when it can prove that execution never began.

For a RunAttempt with an existing NativeRunReference, FlowPlane must conceptually:

1. persist cancellation intent;
2. request provider-native cancellation where supported;
3. observe provider state;
4. reconcile;
5. derive the FlowPlane outcome.

FlowPlane must not mark a provider-running execution as Cancelled merely because a user requested cancellation. Cancellation races are expected.

```text
Cancellation requested
        |
        v
Provider completes successfully before cancellation takes effect
```

Both facts must be preserved: cancellation was requested, and the native execution succeeded. A later technical design must define the FlowPlane outcome rules for such races.

### Timeout semantics

Timeout is FlowPlane orchestration policy. A FlowPlane timeout does not prove that the provider-native execution has terminated.

A timeout may:

- mark the FlowPlane Run outcome according to policy;
- initiate provider cancellation where supported;
- require continued reconciliation of the native execution.

FlowPlane may therefore temporarily know both facts:

```text
FlowPlane policy outcome: TimedOut
Provider-native observation: Running
```

These facts are not contradictory and must not be collapsed. This ADR does not define exact timeout and cancellation race resolution.

### Reconciliation

Reconciliation is a mandatory execution-lifecycle responsibility. Incomplete or ambiguous Runs and RunAttempts must be recoverable by comparing persisted FlowPlane intent and state with provider-native observations.

Reconciliation is required after conditions such as:

- a FlowPlane container or process restart;
- a scheduler or worker restart;
- a lost provider response;
- a stale provider observation;
- temporary provider unavailability;
- cancellation;
- timeout;
- ambiguous dispatch;
- network interruption;
- an orphaned or unexpectedly continuing native execution.

```text
RunAttempt
    |
    v
NativeRunReference
    |
    v
Data-Pipeline Provider
    |
    v
native observation
    |
    v
reconciliation
    |
    v
FlowPlane orchestration state
```

Reconciliation must not blindly overwrite provider-native facts.

### Ambiguous dispatch

A Dispatching attempt may enter this ambiguous situation:

```text
FlowPlane sends an operation that may create native execution state
        |
        v
Provider creates native state
        |
        v
Response is lost
```

FlowPlane must not blindly repeat a provider-side creation operation when doing so could create a duplicate native execution. Ambiguous dispatch requires reconciliation or provider-specific recovery logic.

FlowPlane does not introduce a universal `Unknown` Run or RunAttempt lifecycle state merely to represent uncertainty. The uncertainty or reconciliation condition remains conceptually separate from lifecycle state.

Candidate reconciliation conditions might include:

- clean;
- reconciliation required;
- reconciliation in progress.

These names are illustrative and do not establish a schema.

### Delivery semantics

FlowPlane does not claim exactly-once provider execution. The architecture targets:

- durable execution intent;
- idempotent FlowPlane operations where possible;
- deduplication;
- provider-native correlation;
- reconciliation.

Exactly-once guarantees must not be assumed across FlowPlane and provider network boundaries.

### Schedule-occurrence deduplication

One logical schedule occurrence must create at most one FlowPlane Run. Concurrent scheduler processing must not create duplicate logical Runs for the same schedule occurrence.

The exact PostgreSQL transaction, uniqueness, lease, or locking mechanism remains technical-design work.

### Container and process failure model

FlowPlane-owned runtime processes must be treated as disposable. A process or container restart must be recoverable using:

- PostgreSQL durable FlowPlane state;
- provider-native state and correlation;
- reconciliation.

FlowPlane correctness must not depend on one process or container remaining alive.

This ADR does not decide:

- how many FlowPlane containers or services exist;
- whether the scheduler, workers, and API are separate processes;
- Kubernetes topology;
- Docker Compose topology;
- production deployment topology.

Those questions belong to separate product or deployment architecture work. Provider runtimes such as SSIS are external to this FlowPlane process and container durability model and are not required to be containerized by FlowPlane.

### SSIS as the first proving ground

SSIS is the first provider used to validate this lifecycle architecture. Relevant SSIS behavior includes:

- creating a native execution separately from starting it;
- receiving and preserving the native SSIS execution identifier;
- observing status through SSISDB;
- requesting cancellation through provider-native operations.

SSIS-specific mechanics must remain behind the SSIS provider.

If the first implementation shows that the general lifecycle concepts are insufficient, the project must:

1. identify the implementation pressure;
2. determine whether it is SSIS-specific or genuinely provider-independent;
3. compare the proposed concept with other platforms in the data-pipeline research;
4. revise the general architecture only when broader semantics justify the change.

The first implementation may therefore refine unproven general concepts, but it must not silently make the core SSIS-specific.

## Alternatives considered

### Single Run object with no attempts

Represent logical intent and every provider execution in one Run object. This alternative is not selected because retries and native execution correlation would become ambiguous, and the history of which native execution fulfilled which intent would be lost.

### Copy provider-native status directly into FlowPlane Run status

Use each provider's native status as the FlowPlane Run lifecycle. This alternative is not selected because provider state machines have different semantics and remain under provider authority. It would also collapse provider observations into FlowPlane orchestration state.

### Mark execution Failed whenever the provider becomes unreachable

Treat a connectivity or observation failure as workload failure. This alternative is not selected because provider unavailability does not prove that the native workload failed or stopped.

### Immediate cancellation state

Mark a Run Cancelled as soon as a cancellation request is submitted. This alternative is not selected because cancellation may fail, may not be supported, or may race with successful native completion. Cancellation intent and confirmed outcome must remain distinct.

### Exactly-once execution guarantee

Claim that each logical Run produces exactly one provider-native execution. This alternative is not selected because external provider and network failures make that guarantee unsafe without stronger provider-specific guarantees.

### In-memory execution lifecycle

Keep execution and scheduling state primarily in process memory. This alternative is not selected because FlowPlane processes and containers must be disposable and restartable without losing orchestration intent.

### PostgreSQL and Kafka as co-equal execution-state authorities

Treat PostgreSQL and Kafka as co-authoritative sources for the same orchestration state. This alternative is not selected for the MVP because multiple authorities create reconciliation ambiguity, and Kafka is not required for Minimal FlowPlane. Future Kafka or event-streaming integration remains a separate architecture decision.

## Consequences

Positive consequences include:

- execution intent survives process and container restarts;
- retries are auditable;
- provider-native identity remains preserved;
- provider outages are not falsely reported as workload failures;
- cancellation and timeout races can be represented honestly;
- reconciliation provides a recovery path after crashes and network ambiguity;
- PostgreSQL provides one FlowPlane-owned source of orchestration truth for the MVP;
- the model can be implemented first against SSIS without making SSIS statuses the core lifecycle.

Trade-offs include:

- lifecycle implementation is more complex than one status field;
- reconciliation workers or processes will be required;
- provider adapters need reliable correlation and observation behavior;
- ambiguity can persist while providers are unavailable;
- Run and RunAttempt history increases stored state;
- exact retry and timeout policies require additional design;
- provider-specific edge cases require contract and integration testing.

## Explicitly not decided

This ADR does not decide:

- PostgreSQL table or schema design;
- exact enum or storage representation;
- REST API resources;
- the execution command API;
- exact provider-status mappings;
- detailed SSIS implementation;
- reconciliation frequency;
- reconciliation-worker topology;
- polling versus streaming;
- an event bus;
- Kafka adoption;
- an outbox implementation;
- retry algorithms or backoff;
- exact timeout values;
- exact cancellation-race outcome rules;
- scheduler implementation;
- a PostgreSQL locking mechanism;
- an API idempotency-key format;
- agent architecture;
- process topology;
- Docker Compose topology;
- Kubernetes topology;
- production deployment topology.

## Related documentation

- [FlowPlane Master Architecture and Product Design](../product/FlowPlane-Master-Architecture-and-Product-Design.md)
- [FlowPlane MVP Scope](../product/mvp-scope.md)
- [FlowPlane Architecture Overview](../architecture/overview.md)
- [Data-Pipeline Concepts Research](../domain/data-pipeline-concepts.md)
- [ADR 0001: Separate the Control Plane from the Execution Plane](0001-control-plane-execution-plane-separation.md)
- [ADR 0002: Separate Integration Families](0002-separate-integration-families.md)
- [ADR 0003: Keep FlowPlane Core Independent and Make External Integrations Additive](0003-core-independence-and-progressive-integration.md)
- [ADR 0004: Use a Capability-Based Data-Pipeline Provider Architecture](0004-capability-based-data-pipeline-provider-architecture.md)
