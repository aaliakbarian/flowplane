# FlowPlane SSIS Provider Technical Design

## Document status and authority

This is an implementation-facing technical design for the first FlowPlane Data-Pipeline Provider. It derives implementation direction from the accepted FlowPlane ADRs and the agreed MVP scope; it is not an ADR and does not introduce architectural authority.

The following labels distinguish the status of statements in this document:

- **Accepted FlowPlane architecture** — established by an accepted ADR or the agreed MVP scope.
- **Documented SSIS behavior** — behavior described by current first-party Microsoft documentation.
- **Technical recommendation** — implementation guidance proposed here and subject to validation.
- **MVP implementation scope** — capability needed to deliver the agreed SSIS MVP.
- **Future/stretch capability** — intentionally outside the required MVP path.
- **Unresolved implementation question** — a choice that this design does not settle.

## 1. Purpose and scope

**Accepted FlowPlane architecture:** SSIS is the first proving ground for FlowPlane's capability-based Data-Pipeline Provider architecture. It remains outside the provider-independent FlowPlane Core domain.

**MVP implementation scope:** The SSIS Provider is responsible for:

- connecting to an external SQL Server instance and its SSISDB catalog;
- validating connectivity and effective capability availability;
- discovering folders, projects, packages, parameters, and relevant environment references;
- exposing executable native-definition candidates for explicit FlowPlane registration;
- executing registered SSIS packages with execution-time configuration;
- correlating FlowPlane `RunAttempt` identity with the native SSIS execution ID;
- observing native execution state and exposing relevant diagnostics;
- requesting cancellation and reconciling the result;
- retrieving deployed project/package definitions for read-only Control Flow visualization;
- producing provider-independent `GraphProjection` data for React Flow; and
- reconciling incomplete or ambiguous work after FlowPlane or network failure.

This document defines provider behavior and boundaries. It does not define application code, persistence schema, EF Core mappings, REST resources, final C# interfaces, or frontend components.

## 2. Runtime and deployment boundary

**Accepted FlowPlane architecture:** The control plane and provider runtime remain separate.

```text
Containerized FlowPlane
        |
        | SQL Server connection
        v
SSIS Provider
        |
        v
SSISDB public catalog views and stored procedures
        |
        v
External SQL Server / SSIS runtime
```

SSIS does not run inside FlowPlane containers. Normal SSISDB discovery, execution, observation, cancellation, and project retrieval must not require a custom FlowPlane agent or service installed on the SQL Server host.

The provider is logically part of the FlowPlane execution boundary, but this statement does not decide whether provider code shares a process with other FlowPlane responsibilities. Container count, service decomposition, and production topology remain unresolved elsewhere.

## 3. Primary SSIS integration API

**Technical recommendation:** Use the public SSISDB Transact-SQL catalog views and stored procedures as the primary integration boundary.

Microsoft documents these public APIs for inspecting and operating catalog objects, and notes that SQL Server Management Studio and the managed SSIS API use them for many tasks. This boundary:

- is reachable from a Linux-hosted .NET control plane through a SQL Server connection;
- avoids making Windows-only SSIS runtime libraries a FlowPlane Core dependency;
- preserves SSISDB permissions and native operational identity; and
- avoids private SSISDB tables or undocumented procedures.

`Microsoft.SqlServer.Dts.Runtime` must not be the primary MVP integration mechanism in the Linux FlowPlane deployment. A later Windows-specific helper could be evaluated for authoring or validation capabilities that cannot be provided safely through SSISDB and documented artifact parsing. Such a helper would be optional and would require a separate technical decision.

## 4. `ProviderInstance` connectivity

An SSIS `ProviderInstance` is the configured boundary through which FlowPlane addresses one external SQL Server instance and SSISDB catalog. Conceptually, it references:

- a SQL Server endpoint;
- the SSISDB catalog;
- an authentication configuration and `SecretReference`;
- TLS/trust configuration; and
- SSIS-specific connection or compatibility options.

These are responsibilities, not persisted field definitions.

**MVP implementation scope:** Provider validation must determine, without mutating user workloads:

1. whether the SQL Server endpoint is reachable;
2. whether the configured principal can authenticate;
3. whether SSISDB is reachable;
4. whether the required public catalog objects are present and compatible; and
5. which baseline and optional operations the principal can actually perform.

Connectivity, catalog compatibility, and permission/capability availability are separate results. A reachable server does not imply that discovery or execution is authorized. The exact validation API and the use of active probes versus permission inspection remain technical-design work.

## 5. Authentication and authorization

### Authentication choices

| Choice | Documented behavior and deployment implications | Design position |
| --- | --- | --- |
| SQL authentication | SQL Server authenticates a dedicated SQL login with a password. It works across container/host and trust-domain boundaries but introduces a reusable credential that must be protected and rotated. | **Technical recommendation for the portable MVP baseline:** use a dedicated least-privilege SQL login referenced through `SecretReference`, over a validated encrypted connection. This requires security review and is not a claim that password authentication is universally preferable. |
| Windows/integrated authentication | Microsoft SqlClient supports integrated authentication. A Linux container typically requires Kerberos/domain configuration, service identities, SPNs, tickets, and operational renewal. | Supported deployment option where the operator already has suitable domain infrastructure. Prefer a non-interactive service identity; do not make domain integration a Minimal FlowPlane prerequisite. |
| Microsoft Entra authentication | SQL Server 2022 and later document Entra authentication for supported Windows and Linux on-premises installations as well as SQL Server on Azure Windows VMs. Configuration normally uses Azure Arc; Microsoft also documents a manual certificate/registry/application-registration path for Windows. Availability depends on SQL Server version/topology and infrastructure, and failover cluster instances are not currently supported. | Supported deployment option when the target and operator infrastructure meet Microsoft's prerequisites. Prefer a non-interactive application identity where appropriate, but do not make Entra or Azure Arc the portable self-hosted MVP baseline. |

**Accepted FlowPlane architecture:** Keycloak/OIDC authenticates FlowPlane users. It does not authenticate FlowPlane to SQL Server and its tokens or passwords must not be repurposed as SSIS Provider credentials.

Provider credentials are resolved through controlled secret handling. Connection strings, access tokens, passwords, and sensitive parameter values must never be written to ordinary metadata or logs. Production connections should enable encryption and validate the server certificate; accepting an untrusted certificate should be restricted to an explicitly identified development exception.

### Least-privilege permissions

SSISDB applies row-level security to catalog views and defines object permissions such as `READ`, `MODIFY`, `EXECUTE`, `CREATE_OBJECTS`, `MODIFY_OBJECTS`, and their folder-scoped object variants. `ssis_admin` and `sysadmin` are broad administrative roles and are not the normal FlowPlane runtime permission model. `ssis_logreader` can read operational log views broadly but does not by itself grant package execution or modification.

| Provider capability | Documented permission basis | MVP position |
| --- | --- | --- |
| Discover project/package metadata and parameters | `READ` on the relevant project; view results are filtered by row-level security. | Required for registered/discoverable projects. |
| Read relevant environments/references | `READ` on the project and, when environment values are resolved, `READ` on the environment. | Grant only for selectable environments. |
| Create an execution | `READ` and `EXECUTE` on the project and, when used, `READ` on the referenced environment. | Required. |
| Set pre-start execution parameter values | `READ` and `MODIFY` on the execution instance. | Required when FlowPlane supplies parameters or execution options. |
| Start an execution | `READ` and `MODIFY` on the execution, `READ` and `EXECUTE` on the project, and applicable environment `READ`. | Required. |
| Observe an execution and its permitted diagnostics | Visibility follows SSISDB object permissions and catalog row-level security; operational-log access can also be provided by `ssis_logreader`. | Required for FlowPlane-created executions without granting unrestricted log access by default. |
| Cancel an execution with `catalog.stop_operation` | `READ` and `MODIFY` on the operation/execution, or an administrative role. | Required for MVP cancellation where the principal is authorized. |
| Retrieve a project with `catalog.get_project` | `READ` on the project, or an administrative role. | Required for Control Flow visualization. |
| Deploy a new project | `CREATE_OBJECTS` on the folder; updating an existing project requires `MODIFY` on the project. | Future capability; use a separately elevated deployment principal if enabled. |
| Incrementally deploy packages | `CREATE_OBJECTS` on the project for a new package, or `MODIFY` on the package when updating it, as documented for `catalog.deploy_packages`. | Future capability; exact principal provisioning belongs in operational guidance. |

**Technical recommendation:** Separate routine discovery/execution permissions from later deployment permissions. Exact login/user creation, contained-user choices, grants, and credential rotation belong in deployment and operations documentation and must be validated against every supported SQL Server version.

## 6. Provider capability matrix

This matrix applies ADR-0004 to SSIS. It is not a universal FlowPlane capability catalog.

| SSIS provider responsibility/capability | Classification | Notes |
| --- | --- | --- |
| Provider identity | MVP required | Stable SSIS provider-type identity; representation remains undecided. |
| `ProviderInstance` validation/connectivity | MVP required | Report reachability, catalog compatibility, and effective baseline availability separately. |
| Discovery | MVP required | Discover addressable executable package definitions while preserving native hierarchy and identity. |
| Execution | MVP required | Create, configure, and start a supported SSIS package execution. |
| `NativeRunReference` correlation | MVP required | Preserve the SSIS `execution_id` for each relevant `RunAttempt`. |
| Basic execution observation | MVP required | Observe current/last-known native status and terminality where SSISDB exposes it. |
| Capability description | MVP required | Availability can vary by provider version, instance permissions, definition, and runtime state. |
| Cancellation | MVP required | Request cancellation and reconcile the native result. |
| Parameters | MVP required | Discover metadata and set permitted execution-time values without exposing secrets. |
| Environment references | MVP supporting | Expose applicable native references and record the one selected for execution. |
| Control Flow `GraphProjection` | MVP required | Read-only static projection for the selected package. |
| Executable/unit monitoring | MVP supporting | Overlay task/container timing and result where SSIS logging provides it. |
| Logs/diagnostics | MVP required | Errors, warnings, and relevant messages; richer context is conditional. |
| Metrics/statistics | MVP supporting | Expose available execution/executable statistics without promising all telemetry. |
| Validation | Stretch | Native validation capability requires separate UX and failure-semantics design. |
| Project deployment | Future | ISPAC deployment through `catalog.deploy_project`. |
| Incremental package deployment | Future | DTSX deployment through `catalog.deploy_packages` where supported. |
| Data Flow static projection | Stretch/future | Must not put the agreed MVP at risk. |
| Data Flow runtime statistics | Future | Depends on Performance/Verbose native logging. |

## 7. Discovery design

**Documented SSIS behavior:** The SSISDB catalog exposes permission-filtered hierarchy and metadata through public views.

| Catalog source | Discovery use |
| --- | --- |
| `catalog.folders` | Enumerate visible SSISDB folders. |
| `catalog.projects` | Enumerate visible projects, format version, deployment timestamps, validation information, and current `object_version_lsn`. |
| `catalog.packages` | Enumerate packages, GUID/version metadata, `package_format_version`, and `entry_point`. |
| `catalog.object_parameters` | Discover project/package parameter metadata, types, required/sensitive flags, default/reference metadata. |
| `catalog.environments` | Enumerate environments visible to the principal. |
| `catalog.environment_references` | Discover project-to-environment references and relative/absolute reference type. |
| `catalog.environment_variables` | Inspect permitted variable metadata where required; sensitive values must remain masked. |
| `catalog.object_versions` | Inspect retained project-version identities where useful; Microsoft currently documents project versions in this view. |

```text
Folder
    |
    +-- Project
           |
           +-- Package
```

Discovery results preserve the provider instance, folder, project, and package identity plus useful native facts such as package GUID, package version fields, `entry_point`, package/project format versions, and project `object_version_lsn`. Numeric catalog IDs may assist queries but must not be assumed to be stable across every redeployment or migration.

Only packages that SSIS marks as entry points can be offered as directly executable targets. Non-entry-point packages may still be shown as native project contents when useful, but must not be presented as independently executable.

Discovery produces candidates; it does not synchronize every visible object into PostgreSQL and does not create `Pipeline` records automatically. Query batching, pagination/bounds, refresh, and synchronization policy remain implementation questions.

## 8. Registration boundary

**Accepted FlowPlane architecture:** Discovery and registration are distinct.

```text
SSIS discovery
     |
     v
NativeDefinitionReference candidate
     |
     | selected/imported
     v
FlowPlane Pipeline
```

To re-address a package reliably, the provider-specific portion of `NativeDefinitionReference` must preserve at least the provider instance and the native folder/project/package path. Package GUID and current project/package version facts can support validation and change detection but must not silently redefine the stable FlowPlane `Pipeline` identity.

Before execution or artifact retrieval, the provider re-resolves the reference, confirms that it identifies an entry-point package, and reports a native-definition-not-found/not-executable condition when it no longer does. The exact import UI, bulk registration, rename tracking, and automatic relinking policy are not decided here.

## 9. Parameter and environment handling

SSIS distinguishes project parameters, package parameters, server/environment values, and per-execution values. The provider must preserve those distinctions rather than flattening them into one generic dictionary.

**Documented SSIS behavior:**

- `catalog.object_parameters` describes project/package parameters, including data type, required and sensitive flags, design/default information, whether a value is literal or referenced, and the referenced environment-variable name where applicable.
- `catalog.get_parameter_values` resolves applicable parameter values for a project and optional environment reference. SSISDB masks sensitive results rather than returning them as ordinary plaintext.
- `catalog.environment_references` identifies a project's relative or absolute references. The selected `reference_id` is supplied to `catalog.create_execution`; it is not attached afterward.
- `catalog.set_execution_parameter_value` sets package, project, or system execution parameters before the execution starts. The documented `LOGGING_LEVEL` system parameter is one example.
- `catalog.execution_parameter_values` shows the parameter records associated with one execution, including `required`, `value_set`, and `runtime_override`. It exposes a nonsensitive value in plaintext but returns `NULL` for a sensitive value.

For project-deployment-model parameters, an execution value overrides the server value, and a server value overrides the design value. A required parameter needs a server or execution value; its design default does not satisfy that requirement after deployment. An environment-variable reference resolves a native environment value through the selected environment reference. The provider must use SSISDB resolution behavior rather than reproduce the resolution algorithm in FlowPlane.

**MVP implementation scope:**

1. discover parameter metadata separately from values;
2. identify required inputs and applicable environment references;
3. choose/validate the environment reference before `create_execution`;
4. obtain sensitive user/provider values through controlled secret/input handling;
5. set allowed values before `start_execution`; and
6. preserve the selected reference and non-secret intent in `ExecutionContext`.

Sensitive values intentionally masked by SSISDB remain masked. Raw sensitive values must not be copied into discovery metadata, `ExecutionContext`, audit records, exception messages, or application logs. This document does not define the FlowPlane parameter schema, secret backend, or how one-time values are submitted.

**Technical recommendation:** During recovery of a known Created execution, query `catalog.execution_parameter_values` before deciding whether configuration can continue. `value_set` and `runtime_override` can show whether an assignment occurred, but a masked sensitive value cannot prove that the assigned value equals the original intended secret. If FlowPlane no longer has the required secret input or cannot prove that all intended configuration was applied, automatic start is unsafe; preserve the ambiguity and follow a later reconciliation/operator or new-attempt policy.

## 10. Execution flow

The recommended sequence applies the accepted `Run`/`RunAttempt` lifecycle while respecting SSISDB's create-then-start behavior.

```text
FlowPlane Run
    |
    v
RunAttempt.Created
    |
    v
resolve package, required inputs, and environment reference
    |
    v
RunAttempt.Dispatching
    |
    v
catalog.create_execution(reference_id, ...)
    |
    v
receive execution_id
    |
    v
durably persist NativeRunReference
    |
    v
RunAttempt.Submitted
    |
    v
catalog.set_execution_parameter_value(...), including logging level as needed
    |
    v
catalog.start_execution(execution_id)
    |
    v
observe SSISDB
    |
    v
RunAttempt.Running / Terminal
```

**Documented SSIS behavior:** `catalog.create_execution` creates an execution instance and returns its `execution_id`. Only entry-point packages can be executed directly. The environment `reference_id`, if any, is an input to creation. Parameter values can be changed before start. `catalog.start_execution` starts the created execution, and an execution can be started only once. If the project is redeployed between creation and start, the execution's project version can become outdated and start fails.

**Technical recommendation:** Persist the `NativeRunReference` before setting further values or starting. Do not hold a PostgreSQL transaction open across SQL Server calls. Instead use durable state transitions, idempotent FlowPlane-side command handling where possible, and reconciliation at every remote-call boundary. Final transaction boundaries and concurrency controls require detailed design and failure testing.

## 11. Ambiguous dispatch and recovery

FlowPlane does not claim exactly-once provider execution. The following scenarios apply ADR-0005 to SSIS.

| Scenario | What FlowPlane knows | What SSISDB knows | Safe response |
| --- | --- | --- | --- |
| A. `create_execution` succeeds but its response is lost | Attempt is `Dispatching`; no durable `execution_id` is known. | A new execution exists, normally in Created status, with package/project/caller/time facts. | **Reconciliation required.** The documented procedure exposes no caller-supplied idempotency key. Matching on identity, caller, and time is heuristic rather than proof. Blindly repeating creation is unsafe. Operator/provider-specific recovery may be required; exact ambiguous-create recovery is unresolved. |
| B. ID is persisted; FlowPlane crashes before `start_execution` | Attempt is `Submitted` with a durable native reference; parameter/start progress may need confirmation. | The referenced execution exists, commonly Created unless another actor changed it. `catalog.execution_parameter_values` can expose assignment flags, but sensitive values remain masked. | Query by ID and inspect native status, project revision, environment, and parameter records. Continue automatically only when the intended configuration can be proved; a masked or unavailable required secret can make automatic start unsafe. Exact resume/operator/new-attempt policy remains unresolved. |
| C. `start_execution` succeeds but response is lost | The native ID is known; start result is ambiguous. | The same execution may be Pending, Running, or already terminal. | Query the known ID and reconcile. Do not retry `start_execution` until observation proves that action is valid; SSIS executions can be started only once. |
| D. Project is redeployed between create and start | The attempt refers to an execution created from an earlier project version. | SSISDB retains the execution/project LSN and rejects starting an outdated execution. | Preserve the native failure and changed-version facts. Do not silently execute the new definition under the old intent. A retry, if policy permits, is a new `RunAttempt`; exact policy remains unresolved. |
| E. FlowPlane restarts while the package runs | PostgreSQL retains `RunAttempt` and native ID; observation may be stale. | SSIS continues independently and holds the current/native terminal result. | Query the native ID, restore observation freshness, and reconcile. No SSIS-host agent is required for this recovery path. |

Provider-native execution facts are authoritative. Failed connectivity or a lost response is never itself proof that an SSIS execution failed or did not start.

## 12. Basic execution observation

Use public `catalog.executions` and `catalog.operations` views for basic observation. Preserve the native record separately from normalized FlowPlane lifecycle state.

Useful native facts include:

- `execution_id` and operation identity;
- folder, project, and package names;
- environment reference identity/type/name where present;
- `project_lsn` associated with the execution;
- native status;
- creation, start, and end timestamps;
- caller and executed-as identity;
- stopped-by identity; and
- native server, machine, worker, and process facts where exposed.

The following table is a **technical recommendation**, not final translation code:

| SSIS status | Native interpretation | Possible FlowPlane observation implication |
| --- | --- | --- |
| 1 — Created | Execution instance exists but has not started. | Native reference can support `Submitted`; do not infer active execution. |
| 2 — Running | Native execution is active. | Supports `RunAttempt.Running`. |
| 3 — Canceled | Native operation reached its canceled result. | Candidate terminal attempt result `Cancelled`, subject to orchestration reconciliation. |
| 4 — Failed | Native operation failed. | Candidate terminal attempt result `Failed`. |
| 5 — Pending | Native operation is awaiting execution/resources. | Non-terminal native observation; do not treat as proof of running work. |
| 6 — Ended unexpectedly | Native operation ended abnormally. | Terminal failure-like native result; preserve exact status and diagnostics. |
| 7 — Succeeded | Native operation succeeded. | Candidate terminal attempt result `Succeeded`. |
| 8 — Stopping | Native cancellation/stop is in progress. | Execution remains non-terminal; continue observing. |
| 9 — Completed | Native operation reports completion. | Treat as terminal provider state, preserve exact value, and use applicable native result/diagnostics before deriving orchestration outcome. |

Native status values, timestamps, and diagnostic evidence remain provider-owned facts. Observation freshness/unavailability is tracked separately; the provider becoming unreachable must not translate a last-known Running execution to Failed.

## 13. Cancellation

Use the public `catalog.stop_operation` procedure for provider-native cancellation.

**Accepted FlowPlane architecture:**

1. persist cancellation intent and actor/time context;
2. determine whether a native execution exists and cancellation is applicable;
3. call `catalog.stop_operation` when supported and authorized;
4. observe SSISDB; and
5. reconcile to a final FlowPlane outcome.

A successful procedure call means a stop request was accepted, not that FlowPlane may immediately assert a final `Cancelled` outcome. The provider must handle:

- an already-terminal execution by preserving the observed terminal result;
- an invalid/missing operation ID as a correlation or retention problem, not silent success;
- repeated stop attempts, for which SSISDB may report that the operation is already stopped;
- permission failure as a provider authorization failure; and
- races where the package succeeds or fails before cancellation takes effect.

The exact cancellation-race outcome policy remains governed by later lifecycle design. Both the FlowPlane cancellation request and final provider-native result must be retained.

## 14. Diagnostics and logs

Use public SSISDB views according to the configured principal's visibility:

| View | Intended use |
| --- | --- |
| `catalog.operation_messages` | Operation-level messages, including errors and warnings. |
| `catalog.event_messages` | Package/event messages with source, package path, execution path, event name, and native codes. |
| `catalog.event_message_context` | Property/context captured for an event, including task/container/variable/connection-manager context where logged. |
| `catalog.executables` | Native executable identity, package, and executable path for an execution. |
| `catalog.executable_statistics` | Result, start/end time, duration, execution path, and per-iteration rows. |
| `catalog.execution_component_phases` | Data Flow component phase timing when Performance or Verbose logging is enabled. |
| `catalog.execution_data_statistics` | Data Flow path row counts when Verbose logging is enabled. |

**Basic MVP diagnostics:** bounded execution messages with errors/warnings, native package/executable result, and available task/container timing.

**Rich diagnostics:** event context, detailed execution paths, component phases, row statistics, and provider-specific performance data. Availability depends on native logging level and package behavior.

**Technical recommendation:** Query current/recent native diagnostics on demand with bounded result windows and a short-lived cache. Persist only the FlowPlane-owned execution history plus selected durable summaries/references needed for product behavior. Do not copy the entire SSISDB operational log into PostgreSQL by default. Retention, pagination, refresh, and final caching policy remain unresolved.

## 15. Control Flow package retrieval

For read-only Control Flow visualization, use this provider pipeline:

```text
SSISDB
    |
    | catalog.get_project
    v
ISPAC varbinary
    |
    v
FlowPlane in-memory ISPAC reader
    |
    v
selected DTSX XML
    |
    v
DTSX2 structural parser
    |
    v
SsisControlFlowModel
    |
    v
GraphProjection
    |
    v
React Flow
```

`catalog.get_project` is the primary retrieval mechanism for the currently deployed project artifact. It requires project `READ` permission and returns the project binary stream. No filesystem access to the SSIS host is required.

**Technical recommendation:** Stream/process the artifact in memory and discard it after producing the needed provider model/projection. Persistent ISPAC storage is not an MVP requirement and would require a separate security, retention, and provenance justification.

## 16. ISPAC handling

**Documented SSIS behavior:** An ISPAC is an Open Packaging Conventions package containing one or more DTSX package parts together with `@Project.manifest` and package metadata/relationships.

The reader must:

1. accept the retrieved binary as untrusted input;
2. validate that it is a supported OPC/ZIP package;
3. locate and validate `@Project.manifest` and the requested DTSX part;
4. reject absolute paths, parent traversal, duplicate/conflicting entries, and malformed relationships;
5. bound compressed size, expanded size, entry count, nesting, and processing time;
6. avoid extracting files to the container filesystem by default; and
7. parse the selected DTSX XML with hardened XML settings.

Concrete resource limits remain configurable technical work. Parsing failure must not expose binary/XML contents, secrets, or full connection information in logs.

## 17. DTSX parser boundary

**Technical recommendation:** Use the published DTSX2 XML specification as the primary parsing boundary for the first implementation, while detecting package format/version before parsing.

Inputs to compatibility selection may include:

- `catalog.projects.project_format_version`;
- `catalog.packages.package_format_version`;
- declared package/version properties in the DTSX; and
- namespaces/elements encountered during parsing.

Version-specific parsing adapters may translate supported native formats into the same provider-internal `SsisControlFlowModel`. Format-specific nodes and attributes must not leak into `GraphProjection` as mandatory fields.

The parser should be tolerant of unknown attributes/elements when safe, preserve bounded native detail needed for troubleshooting/extensions, and return partial/unavailable graph results rather than misclassifying content. It must not instantiate or execute SSIS task/component types while parsing. `Microsoft.SqlServer.Dts.Runtime` is not required in the Linux container.

## 18. Control Flow structural model

`SsisControlFlowModel` is a provider-internal static representation, not a FlowPlane Core domain model or storage schema. It represents:

- the package root;
- nested executable hierarchy;
- generic structural role: `Task`, `Container`, or `Unrecognized`;
- provider-native executable type and subtype;
- display name;
- native identities/refIds and parent reference;
- precedence constraints; and
- relevant bounded provider-specific properties and optional layout metadata.

The package root owns the top-level executable collection; this does not introduce a generic FlowPlane node type.

A third-party/custom executable is not automatically `Unrecognized`. If its structural role is safely known, classify it as `Task` or `Container` and preserve its original vendor/native type. Use `Unrecognized` only when the parser cannot safely identify structural role. No catch-all `OtherExecutable` category is introduced.

## 19. Container subtypes

SSIS container subtype remains native detail attached to the generic provider-internal `Container` role. At minimum preserve recognized subtypes for:

- Sequence Container;
- For Loop Container; and
- Foreach Loop Container.

Nested executables and constraints retain their containing hierarchy. Unknown/custom containers remain containers when that role is knowable, with their native type preserved.

## 20. For Loop visualization data

For a recognized For Loop Container, parse static configuration where present and safe:

- initialization expression;
- evaluation expression;
- assignment expression; and
- referenced variables that can be derived without pretending to evaluate arbitrary SSIS expressions.

Runtime overlay may use `catalog.executable_statistics` and execution paths to show completed/current iteration when derivable, per-iteration result, start/end time, and duration.

The provider must not fabricate an expected iteration count. When the loop bound cannot be evaluated reliably, the projection/consumer must support information equivalent to:

```text
Iteration 7
Total unknown
```

Expression evaluation and expected-count inference are not MVP requirements.

## 21. Foreach Loop visualization data

For a recognized Foreach Loop Container, preserve:

- enumerator native type;
- bounded enumerator configuration that the format exposes and the parser understands;
- variable mappings; and
- nested executable hierarchy.

Runtime monitoring may show current/completed iteration, iteration history, and mapped/current values only when native telemetry reliably provides them. `catalog.event_message_context` captures context associated with logged events; it is not a continuous debugger or guaranteed stream of variable values.

The provider/projection must keep these conditions distinct:

- a value was observed;
- a value was unavailable/not observed; and
- a value was observed as absent or null.

Loop-variable display is an SSIS-specific detail. Sensitive values remain masked/redacted even when an event supplies context.

## 22. Control Flow runtime overlay

Static package structure and native runtime observations are combined without changing either source of truth:

```text
DTSX static Control Flow
        +
catalog.executables / catalog.executable_statistics
        +
execution messages and events
        |
        v
runtime Control Flow projection
```

`catalog.executable_statistics` can supply executable result, start/end time, duration, and execution path, including separate rows for loop iterations. `catalog.executables` and event messages provide additional identity/path evidence.

**Technical recommendation:** Correlate primarily with stable native refId/GUID/path evidence available from DTSX and SSISDB, and retain a confidence/availability distinction when exact mapping is not possible. Display-name-only matching is insufficient. The exact identity algorithm and runtime-overlay representation remain unresolved and require fixtures across supported SSIS versions.

## 23. Design-time layout

Published DTSX2 examples place designer metadata in a `DTS:DesignTimeProperties` CDATA value. The embedded XML can contain an `Objects`/`Package` hierarchy with `LayoutInfo` and `GraphLayout`; documented `NodeLayout` records associate a native `Id` with `TopLeft` coordinates and `Size`. Other designer/package versions may add or change node and connector/edge layout records, namespaces, or coordinate conventions, so the layout parser must be version-aware and fixture-tested rather than treating the example shape as a stable semantic schema.

Layout is not runtime semantics. The CDATA can be absent or deleted without preventing the package itself from loading, and its availability, coordinate system, and fidelity can differ by designer/version.

**Technical recommendation:**

1. detect and parse recognized native layout metadata separately from structural semantics;
2. use valid native coordinates as the initial React Flow position;
3. preserve container-relative layout where it can be interpreted correctly; and
4. use deterministic FlowPlane auto-layout when native layout is absent, invalid, unsupported, or incomplete.

The provider must never reject an otherwise parseable Control Flow solely because design-time layout cannot be used. Exact layout-element compatibility and normalization require version-specific tests.

## 24. `GraphProjection` mapping

```text
SsisControlFlowModel
        |
        v
GraphProjection
```

`GraphProjection` is provider-independent presentation data, not the canonical pipeline/domain model and not React Flow serialization.

Conceptually, a projected node can carry:

- structural role (`Task`, `Container`, or `Unrecognized`);
- display name;
- an opaque/provider-qualified native reference;
- provider-native executable type;
- optional layout; and
- optional namespaced SSIS subtype/details.

A projected edge carries source/target references and can attach SSIS-specific precedence details. Generic consumers may render the topology without understanding those extensions; SSIS-aware consumers can render richer semantics.

This document does not decide the final DTO, extension envelope, serialization, or frontend state shape.

## 25. Precedence constraints

Parse and preserve, where the native format exposes them:

- source and target executable identities;
- native constraint/ref IDs;
- evaluation operation (constraint, expression, or their combination);
- result constraint such as success, failure, or completion;
- expression text; and
- logical combination behavior such as AND/OR when multiple incoming constraints participate.

These semantics remain in SSIS provider-specific details. A generic `GraphProjection` edge must not imply that Airflow, dbt, NiFi, or other providers share SSIS evaluation rules.

## 26. Data Flow static parsing architecture

**Future/stretch capability:** A Pipeline Task's internal Data Flow should use a separate provider-internal representation, `SsisDataFlowModel`, capable of later representing:

- components and their native types;
- sources, transformations, and destinations;
- inputs and outputs;
- paths; and
- bounded provider-specific component properties.

```text
SsisControlFlowModel     SsisDataFlowModel
     (orchestration)       (row/data paths)
```

The two models must not be collapsed into one universal SSIS graph that erases their different semantics. A Control Flow node may reference the Data Flow model for a Pipeline Task without embedding the whole data path graph into the Control Flow topology.

Static Control Flow projection is required for MVP. Static Data Flow projection remains stretch/future and may be included only if implementation evidence shows it is inexpensive and does not risk the agreed MVP.

## 27. Data Flow component classification

For future Data Flow projection, use provider-internal structural roles:

- `Source`;
- `Transformation`;
- `Destination`; and
- `Unrecognized`.

A custom/third-party component is not automatically `Unrecognized`. When its structural role can be established from native metadata, retain that role and preserve the original component type as `nativeType`. Use `Unrecognized` only when the role cannot be determined safely.

These roles are SSIS projection concerns and do not establish universal FlowPlane data-pipeline entities.

## 28. Data Flow runtime monitoring

**Future/stretch capability:** Public SSISDB telemetry can support richer Data Flow runtime views:

- `catalog.execution_component_phases` provides component phase/timing data when Performance or Verbose logging is enabled;
- `catalog.execution_data_statistics` provides path row-count data when Verbose logging is enabled; and
- event messages/context can provide component diagnostics when emitted and retained.

Potential displays include component phase timing, rows sent across paths, derived throughput, and native diagnostics. The data is conditional and may be incomplete.

FlowPlane must not silently force Verbose logging solely to animate a UI. Logging level should later be an explicit provider execution option with clear cost/availability implications. When telemetry is absent, the provider reports the capability/data as unavailable rather than inferring zero activity.

## 29. Provider logging level

SSIS logging level affects the cost and availability of diagnostic/runtime data. Microsoft documents that Performance and Verbose levels expose more component data, while Verbose is required for execution data statistics.

**MVP implementation scope:** The provider understands the effective/requested native logging option, may set `LOGGING_LEVEL` before start where authorized, and never promises telemetry that the level cannot produce.

**Unresolved implementation question:** Later product design may expose provider-specific choices equivalent to default/basic versus richer monitoring. This document does not define product preset names, default policy, or who may override catalog/package logging behavior.

## 30. Runtime iteration monitoring

Microsoft documents that `catalog.executable_statistics` contains a row for each executable execution, including each iteration, and that `execution_path` includes iteration information. The SSIS Provider can use that native detail for:

- iteration history;
- duration per iteration;
- result per iteration; and
- current/completed iteration when it can be derived from observations.

Iteration monitoring depends on retained/native telemetry and reliable correlation. It is SSIS-specific provider detail, not a generic FlowPlane Core `RunAttempt` or node lifecycle.

## 31. Project version and graph caching

`catalog.projects.object_version_lsn` identifies the current deployed project version; Microsoft notes that this number is not guaranteed to be sequential.

**Technical recommendation:** Cache parsed artifacts/projections conceptually by:

```text
ProviderInstance
+ folder
+ project
+ object_version_lsn
+ package identity
+ parser/projection format version
```

The parser/projection version component prevents stale reuse when FlowPlane parsing behavior changes. This is a cache-key concept, not a schema or technology decision.

When `object_version_lsn` changes, the previous current projection is potentially stale and must not be presented as the current deployed graph without an explicit stale indication. Cache storage, distribution, eviction, and concrete limits remain unresolved.

## 32. Historical graph correctness

`catalog.executions.project_lsn` records the project version associated with a native execution. Therefore:

```text
Run used project_lsn = A
current project object_version_lsn = B
```

means the currently retrieved ISPAC may not represent the package that actually ran.

**Technical recommendation:** A later implementation should retain a bounded `GraphProjection` snapshot, equivalent parse result, or recoverable version reference associated with the native project revision used by a run. Until that exists, the UI must disclose a version mismatch and must not imply that a current graph is historically exact.

This recommendation does not introduce `PipelineVersion` into the MVP domain and does not require storing the full ISPAC. Snapshot timing, persistence, retention, and whether older project binaries are reliably retrievable remain unresolved.

## 33. Protected, encrypted, or unsupported DTSX

Artifact parsing is optional to execution correctness. FlowPlane remains able to manage and observe a package even when it cannot render its graph.

Conceptual outcomes include:

- graph available;
- graph partially available, with bounded warnings and preserved native types;
- graph unavailable because package protection/encryption is unsupported;
- graph unavailable because format/component structure is unsupported; and
- graph unavailable because artifact retrieval failed.

These are not final enum/API names. The MVP does not require arbitrary package-password acquisition or decryption. Unsupported/protected content must fail closed, avoid secret prompts in logs, and never fall back to executing or instantiating package code to inspect it.

## 34. Future deployment capability

Deployment is outside required MVP scope. The current technical direction is:

```text
ISPAC
  |
  v
catalog.deploy_project
  |
  v
SSISDB project

DTSX package(s)
  |
  v
catalog.deploy_packages
  |
  v
existing SSISDB project
```

`catalog.deploy_project` creates or updates a project and returns a native operation identity. `catalog.deploy_packages` supports incremental package deployment/update in supported SSIS versions and likewise exposes an operation identity. Deployment is observable through SSISDB operations/messages; native validation and deployment errors remain provider-native facts.

Deployment permissions are broader than ordinary read/execute permissions and should be assigned only to a separate deployment capability/principal when enabled. This section does not define a generic FlowPlane `Deployment` domain or approval/promotion workflow.

## 35. Future FlowPlane SSIS designer compatibility

The boundaries above preserve an evolution path in which FlowPlane could later:

- edit/design a package visually;
- maintain an authoring-capable provider-internal SSIS representation;
- serialize or generate DTSX;
- validate the generated native artifact;
- deploy DTSX or ISPAC through public SSISDB APIs; and
- observe executions through the same `RunAttempt`/native-reference boundary.

The read-only parser is not automatically the future authoring model. This design does not decide whether FlowPlane edits DTSX directly, compiles from an intermediate representation, preserves dual representations, or relies on Windows-specific validation helpers. Those questions require separate research and architecture review.

## 36. Version compatibility

Compatibility has multiple independent dimensions:

- SQL Server/SSIS major version and public SSISDB API surface;
- deployed project's `project_format_version`;
- package `package_format_version` and DTSX-declared properties;
- the DTSX/DTSX2 grammar actually encountered; and
- optional task/component types and logging behavior.

Microsoft's published DTSX specifications cover multiple product generations, but publication does not prove that one parser or integration test suite supports every historical artifact.

The following is a **technical recommendation requiring real-environment validation**, not a support promise:

| SQL Server/SSIS version | Initial position | Required evidence before claiming support |
| --- | --- | --- |
| SQL Server 2022 / SSIS 16.x | MVP reference environment | Full discovery, execution, cancellation, diagnostics, artifact retrieval, and parser fixture suite. |
| SQL Server 2019 / SSIS 15.x | Compatibility target | Same provider integration suite plus format-specific fixtures. |
| SQL Server 2025 / SSIS 17.x | Compatibility target after current-driver/API validation | Same suite, with explicit review of changed catalog/DTSX behavior. |
| SQL Server 2017 and earlier | Not initially claimed | Add only through an explicit supported-version change and dedicated fixtures/environments. |

At connection and artifact-read time, the provider must detect known version facts and reject unsupported combinations clearly. It must not silently parse an unknown format as a known one. Unsupported static parsing need not block provider-native execution when the SQL Server itself supports the package; capability availability must make that distinction visible.

## 37. Error model

Provider operations should return structured technical categories plus sanitized native evidence. Illustrative categories include:

- `ProviderUnavailable`;
- `AuthenticationFailed`;
- `PermissionDenied`;
- `NativeDefinitionNotFound`;
- `NativeDefinitionNotExecutable`;
- `RequiredParameterMissing`;
- `EnvironmentReferenceInvalid`;
- `DispatchAmbiguous`;
- `NativeExecutionNotFound`;
- `CancellationRejected`;
- `ArtifactRetrievalFailed`;
- `UnsupportedPackageFormat`;
- `PackageProtectionUnsupported`;
- `GraphParsingPartial`; and
- `GraphParsingFailed`.

Names and transport representation are illustrative. Preserve useful SQL/SSIS error number, operation/execution ID, native status, and safe message/context as provider detail. Do not flatten errors into one string, expose secrets, or infer that every SQL connectivity exception means the package failed.

## 38. Security considerations

Implementation must include:

- least-privilege SQL/SSISDB principals scoped to visible and executable projects;
- `SecretReference`-based credential resolution without ordinary plaintext persistence;
- encrypted SQL connections with certificate validation in production;
- strict separation of Keycloak user identity from provider credentials;
- masking/redaction of sensitive parameters and environment values;
- no secret, connection-string, ISPAC, DTSX, or protected package content in logs;
- hardened XML parsing with DTD/external entity resolution disabled and resource bounds;
- bounded archive download/decompression, traversal and malformed-package defenses;
- sanitized provider error details at API/UI boundaries; and
- audit of connection-configuration changes, execution, cancellation, and future deployment actions.

Downloaded artifacts may contain credentials or connection metadata even when FlowPlane did not expect them. Treat buffers, caches, dumps, telemetry, and temporary files accordingly. This document does not select the secret backend or final audit schema.

## 39. Performance considerations

**Technical recommendations:**

- batch hierarchy/metadata queries and avoid per-row round trips where public views allow it;
- apply bounds/paging to discovery, messages, and execution history;
- do not retrieve an ISPAC during ordinary discovery or execution;
- retrieve artifacts only for graph inspection/refresh, then cache projections by project revision;
- poll active executions more frequently than old terminal executions, with configurable cadence and jitter;
- back off when a provider is unavailable while keeping observations explicitly stale;
- avoid repeatedly scanning unbounded SSISDB history or fetching full message payloads;
- use incremental/native keys or time windows only after their semantics are verified;
- avoid requesting Verbose logging or Data Flow telemetry without explicit need; and
- bound concurrent project downloads and parsing work.

No numeric thresholds, cache technology, or polling scheduler is chosen here.

## 40. Testing strategy

Provider correctness requires tests against real SQL Server/SSISDB environments; mocks alone cannot establish permission, catalog, execution, race, and format behavior.

### Unit and artifact tests

Cover:

- bounded ISPAC/OPC reading and hostile archive cases;
- DTSX2 format detection and parsing;
- Control Flow hierarchy and nested containers;
- precedence-constraint semantics;
- For Loop expressions and runtime-path interpretation;
- Foreach enumerator metadata and variable mappings;
- custom/unknown executable structural classification;
- `SsisControlFlowModel` to `GraphProjection` mapping;
- partial/protected/unsupported artifact outcomes; and
- isolated native-status translation helpers where appropriate.

### Integration tests

Against each claimed SQL Server/SSIS version, cover:

- authentication/connectivity and deliberately insufficient permissions;
- folder/project/package and parameter discovery;
- environment references and masked sensitive values;
- `create_execution`, parameter/logging setup, `start_execution`, observation, and terminal result;
- cancellation, repeated cancellation, and cancellation races;
- restart recovery with a known execution ID;
- controlled lost-response/ambiguous-boundary simulations;
- `catalog.get_project` and graph parsing; and
- future deployment procedures only in deployment-specific tests.

Fixtures should include a basic package, Sequence Container, For Loop, Foreach Loop, nested containers, Data Flow Task, custom/intentionally unsupported executable where feasible, parameters, environment references, failure, long-running behavior, and protected/encrypted content where feasible.

Integration infrastructure must not normalize tests around an `ssis_admin`/`sysadmin` principal. Include least-privilege and denial cases.

## 41. MVP implementation slices

**Technical recommendation:** Implement vertical slices in this dependency order:

1. Provider connectivity, catalog compatibility, and discovery.
2. Pipeline registration support and native-definition re-resolution.
3. Manual execution through create/persist/configure/start with native correlation.
4. Basic observation, terminal reconciliation, execution history, and baseline diagnostics.
5. Durable cancellation intent, native stop, and reconciliation.
6. Restart recovery and ambiguous-dispatch handling for known native references.
7. Full MVP parameter and environment-reference handling.
8. Bounded ISPAC retrieval and DTSX2 Control Flow parsing.
9. `GraphProjection` and read-only React Flow Control Flow monitoring.
10. Specialized container/loop runtime overlays and richer diagnostics.
11. Basic recurring scheduling, occurrence deduplication, and the existing execution path.

Slices 3–6 should be proven with a simple package before graph work. Parameter/environment behavior moves earlier if the first representative packages cannot execute without it.

**Future/stretch:** static Data Flow projection, rich Data Flow runtime monitoring, validation, project deployment, incremental package deployment, and SSIS authoring/designer capabilities.

## 42. Open questions

- Which SQL Server authentication mode is the supported MVP/reference default after deployment and security review?
- What exact least-privilege principal/grant recipes support discovery, execution, observation, cancellation, and graph retrieval across the supported versions?
- Can ambiguous `create_execution` response loss be resolved safely without a client-supplied native correlation key, or must it require operator intervention?
- Which SQL Server/SSIS, project-format, and package-format combinations pass the initial support matrix?
- How reliably can static DTSX executable identities/refIds be mapped to SSISDB runtime executable identity and `execution_path` across versions and containers?
- What graph snapshot/cache storage and retention provides historical correctness without retaining full ISPAC artifacts unnecessarily?
- What active/terminal polling cadence, jitter, and backoff balance freshness with SSISDB load?
- Which native diagnostics must FlowPlane retain after SSISDB retention removes them?
- What is the final provider-independent `GraphProjection` schema and SSIS extension envelope?
- How faithful and version-stable is DTSX design-time layout metadata?
- Does static Data Flow visualization remain stretch, or can evidence justify including it without risking MVP?
- How much Foreach/loop variable context is reliably observable, and how are unavailable/null/sensitive values represented?
- What future provider-internal representation can support SSIS authoring without coupling execution and read-only parsing to an unproven designer model?
- What exact recovery policy applies to a known Created execution after FlowPlane crashes before parameter setup/start?
- How should execution-time secret values be transmitted and disposed of without persisting them in `ExecutionContext`?

## 43. Explicit non-decisions

This design does not decide:

- PostgreSQL schema or EF Core mappings;
- REST API resources or error DTOs;
- frontend React components or React Flow serialization;
- final `GraphProjection` DTO/schema;
- final provider C# interfaces or SDK;
- plugin loading, provider package format, or process isolation;
- generic `Deployment`, `PipelineVersion`, or `Environment` domain concepts;
- Kafka, event bus, or outbox architecture;
- agent architecture;
- Kubernetes, Docker Compose, or production topology;
- future FlowPlane SSIS authoring representation;
- final secret backend;
- final logging presets, polling cadence, cache technology, or retention policy; or
- support for a specific future Data-Pipeline Provider.

## 44. References

All external references below are first-party Microsoft documentation and were reviewed for this design. Product/version applicability still requires verification against the chosen test matrix.

### SSISDB catalog and security

- [Integration Services Language Reference](https://learn.microsoft.com/en-us/sql/integration-services/integration-services-language-reference?view=sql-server-ver17)
- [SSIS Catalog](https://learn.microsoft.com/en-us/sql/integration-services/catalog/ssis-catalog?view=sql-server-ver17)
- [Integration Services catalog views](https://learn.microsoft.com/en-us/sql/integration-services/system-views/views-integration-services-catalog?view=sql-server-ver17)
- [Integration Services catalog stored procedures](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/stored-procedures-integration-services-catalog?view=sql-server-ver17)
- [`catalog.effective_object_permissions`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-effective-object-permissions-ssisdb-database?view=sql-server-ver17)
- [Integration Services roles](https://learn.microsoft.com/en-us/sql/integration-services/security/integration-services-roles-ssis-service?view=sql-server-ver17)
- [Microsoft SqlClient for SQL Server](https://learn.microsoft.com/en-us/sql/connect/ado-net/microsoft-ado-net-sql-server?view=sql-server-ver17)
- [SQL Server authentication with ADO.NET](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/authentication-sql-server?view=sql-server-ver17)
- [SQL Server connection-string syntax](https://learn.microsoft.com/en-us/sql/connect/ado-net/connection-string-syntax?view=sql-server-ver17)
- [SQL Server application security scenarios](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/application-security-scenarios-sql-server?view=sql-server-ver17)
- [Microsoft Entra authentication with SqlClient](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/azure-active-directory-authentication?view=sql-server-ver17)
- [Microsoft Entra authentication for SQL Server](https://learn.microsoft.com/en-us/sql/relational-databases/security/authentication-access/azure-ad-authentication-sql-server-overview?view=sql-server-ver17)

### Discovery, parameters, and versions

- [`catalog.folders`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-folders-ssisdb-database?view=sql-server-ver17)
- [`catalog.projects`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-projects-ssisdb-database?view=sql-server-ver17)
- [`catalog.packages`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-packages-ssisdb-database?view=sql-server-ver17)
- [`catalog.object_parameters`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-object-parameters-ssisdb-database?view=sql-server-ver17)
- [`catalog.execution_parameter_values`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-execution-parameter-values-ssisdb-database?view=sql-server-ver17)
- [`catalog.get_parameter_values`](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-get-parameter-values-ssisdb-database?view=sql-server-ver17)
- [SSIS package and project parameters](https://learn.microsoft.com/en-us/sql/integration-services/integration-services-ssis-package-and-project-parameters?view=sql-server-ver17)
- [`catalog.environments`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-environments-ssisdb-database?view=sql-server-ver17)
- [`catalog.environment_references`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-environment-references-ssisdb-database?view=sql-server-ver17)
- [`catalog.environment_variables`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-environment-variables-ssisdb-database?view=sql-server-ver17)
- [`catalog.object_versions`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-object-versions-ssisdb-database?view=sql-server-ver17)

### Execution, observation, and deployment

- [`catalog.create_execution`](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-create-execution-ssisdb-database?view=sql-server-ver17)
- [`catalog.set_execution_parameter_value`](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-set-execution-parameter-value-ssisdb-database?view=sql-server-ver17)
- [`catalog.start_execution`](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-start-execution-ssisdb-database?view=sql-server-ver17)
- [`catalog.stop_operation`](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-stop-operation-ssisdb-database?view=sql-server-ver17)
- [`catalog.executions`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-executions-ssisdb-database?view=sql-server-ver17)
- [`catalog.operations`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-operations-ssisdb-database?view=sql-server-ver17)
- [`catalog.get_project`](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-get-project-ssisdb-database?view=sql-server-ver17)
- [`catalog.deploy_project`](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-deploy-project-ssisdb-database?view=sql-server-ver17)
- [`catalog.deploy_packages`](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-deploy-packages?view=sql-server-ver17)
- [Deploy Integration Services projects and packages](https://learn.microsoft.com/en-us/sql/integration-services/packages/deploy-integration-services-ssis-projects-and-packages?view=sql-server-ver17)

### Diagnostics and runtime telemetry

- [`catalog.operation_messages`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-operation-messages-ssisdb-database?view=sql-server-ver17)
- [`catalog.event_messages`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-event-messages?view=sql-server-ver17)
- [`catalog.event_message_context`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-event-message-context?view=sql-server-ver17)
- [`catalog.executables`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-executables?view=sql-server-ver17)
- [`catalog.executable_statistics`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-executable-statistics?view=sql-server-ver17)
- [`catalog.execution_component_phases`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-execution-component-phases?view=sql-server-ver17)
- [`catalog.execution_data_statistics`](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-execution-data-statistics?view=sql-server-ver17)
- [Integration Services logging](https://learn.microsoft.com/en-us/sql/integration-services/performance/integration-services-ssis-logging?view=sql-server-ver17)

### ISPAC, DTSX, and graph semantics

- [ISPAC file format specification](https://learn.microsoft.com/en-us/openspecs/sql_data_portability/ms-ispac/fa1145dd-120f-4a58-8086-c1d51b2e70f6)
- [ISPAC package part](https://learn.microsoft.com/en-us/openspecs/sql_data_portability/ms-ispac/6ba76533-a72a-4309-bd61-5627ccc0404f)
- [DTSX2 file format specification](https://learn.microsoft.com/en-us/openspecs/sql_data_portability/ms-dtsx2/fb216aa4-62ab-41c8-a6d5-5b1002739d21)
- [DTSX file format specification](https://learn.microsoft.com/en-us/openspecs/sql_data_portability/ms-dtsx/e5095968-26ea-4824-a717-153ccee642dc)
- [DTSX2 executable structure](https://learn.microsoft.com/en-us/openspecs/sql_data_portability/ms-dtsx2/e46d05c6-2314-4cb5-ba20-25af503ff73f)
- [DTSX2 design-time layout example](https://learn.microsoft.com/en-us/openspecs/sql_data_portability/ms-dtsx2/9e412a0f-209e-46ab-aeb6-2c9ce2ccdd82)
- [DTSX2 precedence constraints](https://learn.microsoft.com/en-us/openspecs/sql_data_portability/ms-dtsx2/2b249b86-4183-4ec4-ace1-19bd2d4974bb)
- [SSIS Control Flow](https://learn.microsoft.com/en-us/sql/integration-services/control-flow/control-flow?view=sql-server-ver17)
- [SSIS precedence constraints](https://learn.microsoft.com/en-us/sql/integration-services/control-flow/precedence-constraints?view=sql-server-ver17)
- [Integration Services containers](https://learn.microsoft.com/en-us/sql/integration-services/control-flow/integration-services-containers?view=sql-server-ver17)
- [For Loop Container](https://learn.microsoft.com/en-us/sql/integration-services/control-flow/for-loop-container?view=sql-server-ver17)
- [DTSX2 Pipeline Task object data](https://learn.microsoft.com/en-us/openspecs/sql_data_portability/ms-dtsx2/65e8a8ad-6745-44ca-8668-8549aed0190f)

## Related FlowPlane documentation

- [Master Architecture and Product Design](../../product/FlowPlane-Master-Architecture-and-Product-Design.md)
- [MVP Scope](../../product/mvp-scope.md)
- [Architecture Overview](../overview.md)
- [Data-Pipeline Concepts Research](../../domain/data-pipeline-concepts.md)
- [MVP Domain Model](../../domain/mvp-domain-model.md)
- [ADR 0001: Separate the Control Plane from the Execution Plane](../../adr/0001-control-plane-execution-plane-separation.md)
- [ADR 0002: Separate Integration Families](../../adr/0002-separate-integration-families.md)
- [ADR 0003: Keep FlowPlane Core Independent and Make External Integrations Additive](../../adr/0003-core-independence-and-progressive-integration.md)
- [ADR 0004: Use a Capability-Based Data-Pipeline Provider Architecture](../../adr/0004-capability-based-data-pipeline-provider-architecture.md)
- [ADR 0005: Define Execution Lifecycle, Authority, and Reconciliation](../../adr/0005-execution-lifecycle-authority-and-reconciliation.md)
