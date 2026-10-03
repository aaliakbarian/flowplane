I am building an open-source project called **FlowPlane**.

I want you to act as a **principal software architect, distributed-systems architect, data-integration architect, and open-source project advisor** and help me design this project from first principles.

Do not simply agree with my assumptions. Challenge them, identify architectural risks, compare alternatives, and recommend a pragmatic design that can start as an MVP but scale into a serious open-source platform.

# 1. Product vision

FlowPlane is intended to be an **open-source, cross-platform data workflow orchestration, execution, scheduling, monitoring, and visual-design platform**.

It started from the idea of building a modern web UI for SQL Server Integration Services (SSIS), but I do NOT want FlowPlane to become tightly coupled to SSIS, SSISDB, SQL Server, or SQL Server Agent.

The long-term vision is:

> FlowPlane provides a centralized control plane for designing, scheduling, executing, and monitoring data workflows across heterogeneous execution environments.

SSIS should be the **first major execution provider**, not the architectural foundation of the entire product.

Potential future providers/execution technologies could include other workflow engines, custom workers, scripts, command-line jobs, Python workloads, or other data-integration systems.

Do not assume I will implement those providers now. The architecture should simply avoid making them impossible later.

# 2. Platform independence

"Platform independent" has a specific meaning for this project.

The **FlowPlane control plane must be capable of running on both Linux and Windows and should be container-friendly**, including Docker and potentially Kubernetes deployments.

The control plane should include things such as:

- React web application
- ASP.NET Core API
- scheduler
- dispatcher
- metadata services
- authentication/authorization
- monitoring
- API services
- background workers
- PostgreSQL

However, the actual execution environment may have platform-specific requirements.

For example, SSIS itself has platform limitations. Current Microsoft documentation states that SSIS on Linux does not support SSIS Catalog, SQL Agent scheduling, Windows Authentication, SSIS Scale Out, and several other features. Microsoft also states that installing SSIS in containers is not supported.

Therefore I want FlowPlane to distinguish between:

**Control Plane**

and

**Execution Plane**

Do not design the system under the assumption that SSIS must run inside the same container as FlowPlane.

Instead, investigate an architecture such as:

    FlowPlane Control Plane
          |
          +-- PostgreSQL
          +-- API
          +-- Scheduler
          +-- Dispatcher
          +-- Web UI
          |
          +-------------------+
                              |
                     Execution Plane
                              |
             +----------------+----------------+
             |                |                |
        Windows Agent    Linux Agent     Remote SSIS
             |                |                |
            SSIS             SSIS        SQL Server/SSIS

Critically evaluate this architecture.

# 3. Control Plane vs Execution Plane

I want FlowPlane to own:

- workflows
- workflow versions
- schedules
- triggers
- execution requests
- execution state
- retries
- timeouts
- dependencies
- concurrency
- queues
- workers/agents
- monitoring
- alerting
- users
- roles
- permissions
- audit history
- deployment metadata
- environments
- connections
- secrets references
- provider configuration

The execution provider should own technology-specific behavior.

For SSIS this may include:

- DTSX
- ISPAC
- SSISDB
- SSIS runtime
- SSIS-specific parameters
- SSIS-specific events
- SSIS-specific execution messages
- SSIS-specific data-flow statistics

I want a clean boundary between these two worlds.

# 4. SSIS should be a provider

Investigate a design such as:

    IExecutionProvider

or:

    IWorkflowProvider

or:

    IIntegrationProvider

with an SSIS implementation such as:

    SsisProvider

Potential responsibilities:

    DiscoverProjects()
    DiscoverPackages()
    GetPackage()
    DeployPackage()
    Validate()
    Execute()
    Cancel()
    GetExecution()
    GetLogs()
    GetEvents()
    GetStatistics()

But do not blindly use these exact interfaces.

Design the provider abstraction carefully so that the core domain does not become an abstraction leak.

Explain which concepts should be:

- provider-independent
- provider-specific
- translated through adapters
- represented as capabilities

I am particularly interested in a **capability model**.

For example:

    SupportsScheduling
    SupportsCancellation
    SupportsStreamingLogs
    SupportsDataFlowStatistics
    SupportsDeployment
    SupportsValidation
    SupportsParameters
    SupportsEnvironments
    SupportsRemoteExecution
    SupportsWindowsAuthentication

Evaluate whether this is better than simply having a generic provider interface.

# 5. PostgreSQL

I am considering PostgreSQL as FlowPlane's own metadata database.

I do NOT want to simply replicate SSISDB into PostgreSQL.

Determine what should be authoritative in PostgreSQL versus what should remain authoritative in the execution provider.

For example:

PostgreSQL may own:

- FlowPlane workflows
- workflow versions
- schedules
- agents
- execution requests
- orchestration state
- users
- permissions
- audit records
- provider configuration
- deployment records
- metadata
- relationships
- cached provider information

SSISDB may remain authoritative for:

- SSIS package execution
- SSIS operation state
- SSIS-specific messages
- SSIS events
- SSIS data-flow statistics
- SSIS-specific catalog information

Research current Microsoft documentation and recommend the correct boundary.

# 6. Scheduling

One of the main goals is to avoid making SQL Server Agent the central scheduler.

Traditionally an SSIS environment might look like:

    SQL Server Agent
          |
          v
       SSIS
          |
          v
       SSISDB

I want FlowPlane to potentially provide:

    FlowPlane Scheduler
           |
           v
    Execution Dispatcher
           |
           v
       Agent/Provider
           |
           v
          SSIS

This would allow centralized scheduling and monitoring across environments.

Investigate:

- scheduler architecture
- cron-like scheduling
- timezone handling
- daylight saving time
- missed executions
- retries
- backoff
- concurrency limits
- execution priorities
- dependencies
- DAG execution
- manual execution
- event-based triggers
- API triggers
- future event triggers
- distributed scheduling
- scheduler high availability
- leader election
- duplicate execution prevention
- idempotency
- execution leases
- recovery after scheduler restart

Do not assume a simple background timer inside ASP.NET Core is sufficient.

Recommend a production-grade but pragmatic architecture.

# 7. Agents

I am considering introducing a FlowPlane Agent.

Example:

    Agent: PROD-WIN-01
    OS: Windows
    Capabilities:
      - SSIS
      - PowerShell
      - filesystem

or:

    Agent: ETL-LINUX-01
    OS: Linux
    Capabilities:
      - SSIS
      - Python
      - shell

Investigate whether agents should:

- poll the control plane
- maintain WebSocket/gRPC connections
- use HTTP long polling
- use a message broker
- receive execution commands
- stream logs
- report heartbeats
- report capabilities
- report resource utilization

Compare:

- REST polling
- gRPC
- WebSockets
- message brokers

Recommend the simplest architecture appropriate for the MVP and explain how it could evolve.

# 8. Execution lifecycle

Design a complete execution lifecycle:

    Scheduled
       ↓
    Queued
       ↓
    Dispatched
       ↓
    Assigned
       ↓
    Starting
       ↓
    Running
       ↓
    Succeeded / Failed / Cancelled / TimedOut

Explain:

- state ownership
- state transitions
- persistence
- retries
- recovery
- cancellation
- agent crashes
- API crashes
- scheduler crashes
- network failures
- duplicate commands
- stale agents
- orphan executions

I want the design to be resilient to failure.

# 9. Monitoring and logs

I previously considered using SSISDB directly for logs and monitoring.

Evaluate the correct architecture.

For SSIS:

    FlowPlane
       |
       v
    SsisProvider
       |
       v
    SSISDB catalog views/procedures

Potential SSIS sources include:

- catalog.executions
- catalog.operations
- catalog.operation_messages
- catalog.event_messages
- catalog.event_message_context
- catalog.execution_data_statistics

Determine which information should:

1. be queried live from the provider
2. be copied into PostgreSQL
3. be cached
4. be streamed into FlowPlane
5. remain provider-specific

I want the UI to eventually support:

- live execution status
- live logs
- errors
- warnings
- execution duration
- throughput
- data-flow statistics
- task-level status
- historical executions
- execution comparison
- failure analysis

Design a monitoring architecture that works for SSIS but does not force future providers to expose identical concepts.

# 10. Visual workflow designer

For the frontend I am considering React + TypeScript and **React Flow** for the visual designer.

React Flow is a node-based React library with custom nodes, edges, zooming, panning, selection, minimap, controls, etc.

I want to support at least two related visual concepts:

### Workflow Designer

Example:

    Start
      |
      v
    Extract Customers
      |
      v
    Transform
      |
      v
    Load Customers
      |
      v
    Validate

### Data Flow Designer

Example:

    OLE DB Source
          |
          v
       Lookup
          |
          v
    Derived Column
          |
          v
    OLE DB Destination

Determine whether these should be separate editors or one generalized graph engine.

Design:

- node model
- edge model
- ports/handles
- node types
- properties
- validation
- configuration panels
- undo/redo
- copy/paste
- grouping
- annotations
- layout
- versioning
- serialization
- schema evolution
- provider-specific nodes
- provider-independent nodes

Most importantly, determine whether the React Flow representation should be the **canonical workflow model** or merely a visualization/editor representation of a backend domain model.

I strongly prefer avoiding a frontend-specific data model becoming the canonical backend model unless you can justify it.

# 11. SSIS package designer

Investigate how an SSIS package could be represented in the FlowPlane designer.

Potentially:

    SSIS Package
       |
       +-- Control Flow
       |
       +-- Data Flows
       |
       +-- Variables
       |
       +-- Parameters
       |
       +-- Connection Managers
       |
       +-- Event Handlers
       |
       +-- Expressions

Determine whether FlowPlane should:

A. Directly edit DTSX XML

B. Build its own intermediate domain model and compile/export to DTSX

C. Maintain both a FlowPlane model and provider-specific representation

D. Use another approach

I want a serious analysis of the tradeoffs.

# 12. Deployment model

Design deployment scenarios for:

### Local development

    Docker Compose
       |
       +-- React
       +-- API
       +-- PostgreSQL
       +-- Scheduler
       +-- Agent

### Linux production

    Kubernetes / Docker
       |
       +-- FlowPlane control plane
       +-- PostgreSQL
       +-- agents

### Windows production

    Windows Server
       |
       +-- FlowPlane
       +-- Windows Agent
       +-- SSIS

### Hybrid

    Linux/Kubernetes Control Plane
             |
             +-- Windows SSIS Agent
             |
             +-- Linux SSIS Agent
             |
             +-- Remote SQL Server / SSIS

Explain which components should be containers and which should not.

# 13. Technology choices

Current preferred technologies:

Frontend:

- React
- TypeScript
- React Flow / @xyflow/react
- modern component library
- REST API or possibly REST + WebSocket/gRPC where appropriate

Backend:

- ASP.NET Core
- C#
- PostgreSQL
- Entity Framework Core where appropriate

But I want you to challenge these choices if there are better alternatives.

Research:

- ASP.NET Core background services
- Hangfire
- Quartz.NET
- Temporal
- MassTransit
- RabbitMQ
- Kafka
- Redis
- PostgreSQL-based queues
- gRPC
- WebSockets

Do not automatically recommend adding these technologies.

For every infrastructure dependency, explain whether it is:

- necessary for MVP
- useful later
- unnecessary complexity

# 14. Open-source architecture

The project should be designed as a credible open-source project.

Consider:

- repository structure
- modular architecture
- plugin/provider architecture
- licensing considerations
- contribution model
- configuration
- secrets
- migrations
- Docker Compose
- development environment
- test strategy
- integration tests
- provider contract tests
- documentation
- architecture decision records
- semantic versioning
- API compatibility

Suggest an appropriate repository structure.

# 15. Security

Design:

- authentication
- authorization
- RBAC
- service-to-agent authentication
- agent registration
- credentials
- secret references
- encryption
- audit logging
- tenant/workspace isolation if relevant
- least privilege
- remote SQL credentials
- Windows credentials
- API tokens
- certificate-based authentication if appropriate

Never store raw database passwords in PostgreSQL without an explicit secure design.

# 16. Multi-environment support

I want FlowPlane to support environments such as:

    Development
    Test
    UAT
    Production

Potentially:

    Project
       |
       +-- DEV
       +-- UAT
       +-- PROD

Investigate how configuration promotion should work.

For example:

    Development Workflow
           |
           v
        Deploy
           |
           v
           UAT
           |
           v
        Promote
           |
           v
        Production

Consider:

- environment variables
- parameters
- secrets
- connection references
- approvals
- deployment versions
- rollback
- immutable workflow versions

# 17. Domain model

Create a proposed domain model including:

- Organization
- Workspace
- Environment
- Agent
- Provider
- Connection
- Workflow
- WorkflowVersion
- Node
- Edge
- Schedule
- Trigger
- Execution
- ExecutionAttempt
- ExecutionEvent
- ExecutionLog
- Deployment
- Artifact
- SecretReference
- User
- Role
- Permission
- AuditEvent

But do not blindly include every object.

Explain which entities are essential for MVP and which should be postponed.

# 18. API design

Propose REST APIs such as:

    /api/workflows
    /api/workflows/{id}
    /api/workflows/{id}/versions
    /api/executions
    /api/executions/{id}
    /api/executions/{id}/logs
    /api/agents
    /api/providers
    /api/environments
    /api/deployments
    /api/schedules

Design the API around the domain, not around SSISDB tables.

Include:

- request/response examples
- pagination
- filtering
- sorting
- idempotency
- optimistic concurrency
- error model
- authentication
- versioning

# 19. Observability

Design:

- application logs
- structured logging
- metrics
- traces
- execution metrics
- agent heartbeat metrics
- scheduler metrics
- health endpoints
- OpenTelemetry

Determine what should be stored in PostgreSQL versus emitted to an observability stack.

# 20. MVP

Most importantly, define a realistic MVP.

I do NOT want to build the entire platform before getting something useful.

Propose a phased roadmap:

### Phase 1
Minimal working platform

### Phase 2
SSIS integration

### Phase 3
Scheduling and monitoring

### Phase 4
Visual workflow/data-flow designer

### Phase 5
Agents and distributed execution

### Phase 6
Provider/plugin ecosystem

For each phase identify:

- features
- architecture
- database changes
- frontend work
- backend work
- risks
- what NOT to build

# 21. Architectural principles

Evaluate the project against these principles:

1. Control plane vs execution plane separation
2. Provider independence
3. Platform independence
4. API-first architecture
5. PostgreSQL-owned application metadata
6. SSISDB as an external provider rather than the application database
7. Stateless API where practical
8. Durable scheduler state
9. Agent-based execution
10. Provider capabilities rather than provider-specific assumptions
11. Frontend independent from provider implementation
12. React Flow as an editor/view rather than automatically the domain model
13. Container-friendly control plane
14. OS-specific execution agents
15. Observability-first design
16. Security by default

# 22. Important research requirement

Use current authoritative documentation wherever possible.

Especially verify current Microsoft documentation regarding:

- SSIS on Windows
- SSIS on Linux
- SSISDB
- SQL Server Agent
- SSIS execution APIs
- SSIS runtime
- DTSX/ISPAC
- SSIS containers
- SSIS limitations on Linux
- SSIS deployment
- SSIS monitoring/logging

Also investigate current React Flow documentation for its capabilities and limitations.

Do not present assumptions as facts.

Clearly distinguish:

- documented behavior
- architectural recommendation
- opinion
- future possibility

# 23. Deliverables

I want the final output to contain:

1. Executive architecture summary
2. Architecture diagram
3. Control-plane architecture
4. Execution-plane architecture
5. Provider abstraction
6. SSIS provider architecture
7. Scheduler architecture
8. Agent architecture
9. Monitoring/logging architecture
10. React Flow designer architecture
11. Domain model
12. PostgreSQL schema proposal
13. REST API proposal
14. Security architecture
15. Deployment architecture
16. Repository structure
17. MVP plan
18. Future roadmap
19. Major architectural risks
20. Explicit recommendations
21. Architecture Decision Records for the most important decisions

Be pragmatic.

The goal is not to build an enterprise distributed system on day one.

The goal is to build a **small, clean, open-source MVP whose architecture can evolve into a serious cross-platform data workflow orchestration platform without requiring a rewrite.**

The project name is:

# FlowPlane

Please use **FlowPlane** consistently throughout the architecture and examples.