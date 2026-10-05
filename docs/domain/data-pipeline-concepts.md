# Data-Pipeline Concepts Research

## 1. Research purpose and status

This document is domain research for FlowPlane. It compares the native concepts of data-pipeline platforms and adjacent integration systems to inform a future canonical vocabulary and the research behind ADR-0002. It is not an ADR, is not architectural authority, and does not define final interfaces, database schemas, APIs, or implementation technologies.

Research snapshot: 2026-10-04.

The [master architecture document](../product/FlowPlane-Master-Architecture-and-Product-Design.md) remains authoritative for overall product and architectural intent. The [MVP scope](../product/mvp-scope.md) defines the agreed MVP direction, the [architecture overview](../architecture/overview.md) summarizes the current architecture, and [ADR-0001](../adr/0001-control-plane-execution-plane-separation.md) records the accepted Control Plane / Execution Plane separation.

This research uses the following labels:

- **Documented external behavior** describes a platform using its current first-party documentation.
- **Research observation** is a cross-platform interpretation drawn from those documented behaviors.
- **Candidate FlowPlane concept** is vocabulary for later evaluation, not accepted architecture.
- **Recommendation** is guidance for the future decision process, not a decision.
- **Open question** identifies a matter that remains unresolved.

Platform-native terms do not automatically become FlowPlane core concepts. Similar words may have incompatible meanings, and different words may represent similar roles. The matrices therefore preserve native terminology and use an em dash when a useful mapping is not established.

The researched systems belong to distinct integration families:

- SQL Server Integration Services (SSIS), Azure Data Factory, Microsoft Fabric Data Factory, IBM DataStage, Informatica Cloud Data Integration, AWS Glue, Google Cloud Data Fusion, Pentaho Data Integration, Apache Airflow, Dagster, Databricks Lakeflow Jobs, dbt, SQLMesh, Airbyte, Fivetran, and Apache NiFi are evaluated as data-pipeline platforms.
- OpenMetadata is a metadata catalog platform, and OpenLineage is a lineage specification and event model. Neither is an execution provider.
- GitHub, GitLab, Forgejo, Gitea, and generic Git repositories are source-control integrations, not data-pipeline providers.
- GitHub Actions, GitLab CI, Forgejo Actions, Jenkins, Tekton, and other external CI/CD systems are delivery integrations, not data-pipeline providers.

## 2. Platform categories

The categories below are research aids rather than exclusive product classifications. Some systems span more than one category.

| Category | Systems reviewed | Characteristic domain emphasis |
| --- | --- | --- |
| Data integration / ETL | SSIS, Azure Data Factory, Microsoft Fabric Data Factory, IBM DataStage, Informatica Cloud Data Integration, AWS Glue, Google Cloud Data Fusion, Pentaho Data Integration | Movement and transformation of data through packages, pipelines, mappings, jobs, stages, or steps; often includes connection and runtime infrastructure concepts. |
| Workflow orchestration | Apache Airflow, Dagster, Databricks Lakeflow Jobs | Dependency graphs, schedules, triggers, task execution, retries, and operational state. Dagster also gives data assets a central role. |
| Transformation | dbt, SQLMesh | Declarative transformation definitions, dependency graphs, environments or targets, validation, and version-aware deployment. |
| Ingestion / replication | Airbyte, Fivetran | Configured source-to-destination connections, streams or schemas, sync state, incremental replication, and managed connector behavior. |
| Dataflow / integration | Apache NiFi | Long-running flow-based processing with processors, queues, routing relationships, back pressure, and provenance. |
| Metadata / lineage | OpenMetadata, OpenLineage | Description and observation of pipelines, runs, datasets, ownership, discovery, and lineage rather than workload execution. |

**Research observation:** These categories expose different domain models because they optimize for different work. An orchestrator tends to make a dependency graph and task instances prominent; an ingestion system tends to make connections, streams, and synchronization state prominent; a transformation system tends to make models, dependencies, and environments prominent; and a flow-based system may treat queued data packets as part of its runtime semantics. A common model that assumes every provider has the same inner task, graph, artifact, environment, or deployment lifecycle would erase material differences.

## 3. Platform concept matrix

The matrix is split into definition-time and lifecycle views for readability. Entries describe native concepts, not proposed one-to-one FlowPlane mappings.

### 3.1 Definition and structure

| Platform | Native top-level definition or executable | Hierarchy / container | Graph representation | Executable unit | Data unit / asset | Source / destination | Connection / resource | Parameters / configuration | Environment |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| [SSIS](https://learn.microsoft.com/en-us/sql/integration-services/integration-services-ssis-packages?view=sql-server-ver17) | Package; project groups packages for project deployment | Project, package, sequence/loop containers | Control Flow; Data Flow inside a Data Flow task | Task, container, package | External tables/files and in-flight rows; no single package-level asset entity | Source and destination components | Connection Manager | Project/package parameters, variables, configurations | SSISDB environment and environment references in the project deployment model |
| [Azure Data Factory](https://learn.microsoft.com/en-us/azure/data-factory/introduction) | Pipeline | Factory/folder; nested activities | Pipeline dependency graph; mapping data flow graph | Activity | Dataset | Copy/activity source and sink | Linked Service; Integration Runtime provides compute/network execution infrastructure | Pipeline, dataset, linked-service, and data-flow parameters; variables | Factory and deployment environment are operational concepts rather than one universal entity |
| [Microsoft Fabric Data Factory](https://learn.microsoft.com/en-us/fabric/data-factory/data-factory-overview) | Data pipeline, Dataflow Gen2, or Copy job | Workspace/item; activity containers within a pipeline | Pipeline activity graph; dataflow transformation graph | Activity or dataflow transformation | Table, file, or other data selected inline by an activity; Fabric eliminates ADF Dataset entities | Connector source and destination | Connection; on-premises data gateway where required; no ADF Integration Runtime equivalent | Pipeline parameters/variables and item configuration | Workspace/deployment stage may represent lifecycle context; semantics differ by feature |
| [IBM DataStage](https://www.ibm.com/docs/en/ws-and-kc?topic=flows-datastage-stages) | DataStage flow/job | Project/folder; orchestration pipeline can contain job nodes | Stages connected by links; orchestration pipeline connections | Stage or job node | Table/file/message data represented through stages and links | Source and target stages | Platform connections | Parameters and parameter sets | Project/space and target deployment context; not one portable environment abstraction |
| [Informatica Cloud Data Integration](https://docs.informatica.com/integration-cloud/cloud-data-integration/current-version/asset-management/asset-migration.html) | Mapping, mapping task, or taskflow | Project/folder; taskflow nesting | Mapping transformation graph; taskflow steps/links | Transformation or task | Source/target object and fields | Source and target objects | Connection; runtime environment supplies execution resources | Mapping/task parameters, parameter files, taskflow inputs | Runtime environment plus organization/project lifecycle context |
| [AWS Glue](https://docs.aws.amazon.com/glue/latest/dg/components-key-concepts.html) | ETL job or workflow | Workflow groups jobs, crawlers, and triggers | Workflow graph; visual job graph | Job, crawler, or trigger | Data Catalog table and underlying store object | Job source and target | Glue connection; execution role/resources | Job arguments and workflow run properties | Account/Region and deployment context; no provider-wide environment entity |
| [Google Cloud Data Fusion](https://docs.cloud.google.com/data-fusion/docs/concepts/overview) | Pipeline | Instance and namespace | Directed acyclic graph of plugin nodes | Source, transformation, analytics, action, or sink plugin | External datasets handled through plugins | Source and sink plugins | Connection configuration/plugin properties | Runtime arguments, macros, preferences | Instance/namespace and deployment target |
| [Pentaho Data Integration](https://docs.pentaho.com/pdia-data-integration/basic-concepts-of-pdi) | Transformation or job | Job can invoke jobs/transformations; repository folders | Transformation steps/hops; job entries/hops | Step or job entry | Rows plus external tables/files | Input/output steps | Database and other connection definitions | Parameters and variables | Repository/server and configuration context; no single cross-installation environment entity |
| [Apache Airflow](https://airflow.apache.org/docs/apache-airflow/stable/core-concepts/) | DAG | DAG, TaskGroup, tasks | DAG of task dependencies | Task created from an operator, sensor, or other task API | Asset/dataset concepts can participate in scheduling; not every task has an asset | Provider-specific hooks/operators | Connection, hook, pool, executor resources | Params, Variables, configuration, connection extras | Airflow deployment and provider-specific contexts; no universal promotion environment object |
| [Dagster](https://docs.dagster.io/guides/build/assets/defining-assets) | Asset definitions and jobs | Definitions/code location; graphs and asset groups | Asset dependency graph or op graph | Asset, op, or graph-backed unit | Asset is first-class | Asset dependencies and I/O managers represent inputs/outputs | Resource and I/O manager | Config, resource configuration, partitions | Deployment/code location plus user-modeled resources and partitions |
| [Databricks Lakeflow Jobs](https://docs.databricks.com/aws/en/jobs) | Job | Job contains tasks; tasks may call notebooks, pipelines, or other jobs | Task DAG | Task | Tables/files/volumes and other workload-specific objects | Task-specific inputs/outputs | Compute, warehouse, connection, and task resources | Job/task parameters and dynamic value references | Workspace, job environment, and deployment target concepts |
| [dbt](https://docs.getdbt.com/docs/build/models) | dbt project and selected command/job | Project, packages, resource paths, groups | Dependency DAG among models, sources, tests, and other resources | Model, test, seed, snapshot, operation | Relation produced by a model; source relation | Source and model/ref dependencies | Adapter/profile/target and source configuration | Project configuration, variables, environment variables | Target and deployment environment |
| [SQLMesh](https://sqlmesh.readthedocs.io/en/stable/concepts/overview/) | Project of models evaluated through plans | Project and model dependencies | Model dependency graph | Model or audit evaluation | Model output table/view | Model dependencies and external models | Gateway/engine connection | Model configuration, macros, variables, gateway settings | Named environment is a first-class planning/deployment concept |
| [Airbyte](https://docs.airbyte.com/platform/using-airbyte/configuring-schema) | Connection between a source and destination | Organization/workspace/connection; connection contains streams | Source-to-destination connection with selected streams, not a general task DAG | Sync of a connection/stream | Stream and fields | Source and destination | Source/destination connector configuration | Connection catalog, selected streams, sync modes, connector configuration | Workspace and deployment context; no universal promotion environment entity |
| [Fivetran](https://fivetran.com/docs/core-concepts) | Connection/connector sync | Account/group/connection; destination schemas/tables | Source-to-destination replication mapping, not a general task DAG | Managed sync | Schema/table/column | Connector source and destination | Connector and destination configuration | Connection setup, schema selection, sync settings | Group/destination and deployment model context |
| [Apache NiFi](https://nifi.apache.org/nifi-docs/user-guide.html) | Dataflow, commonly scoped by a Process Group | Nested Process Groups | Processors joined by relationships and Connections | Processor | FlowFile is a runtime data packet, not automatically a domain data asset | Ingress/egress processors and ports | Controller Service; Connection is also a queued edge | Processor properties and Parameter Contexts; the NiFi 1.x Variable Registry is removed in NiFi 2 | Process groups, external flow-versioning context, and deployment-specific Parameter Contexts |
| [OpenMetadata](https://github.com/open-metadata/OpenMetadata/blob/main/openmetadata-spec/src/main/resources/json/schema/entity/data/pipeline.json) | Catalog `Pipeline` entity | Pipeline service and pipeline/task metadata | Task relationships and cross-entity lineage | Cataloged task metadata; not executed by OpenMetadata as a pipeline provider | Catalog entities such as tables, topics, dashboards, and containers | Lineage endpoints | Service connection for metadata ingestion | Metadata ingestion configuration | Catalog domains/services; not an execution environment model |
| [OpenLineage](https://openlineage.io/docs/next/spec/object-model/) | `Job` metadata observed through events | Namespace and optional parent relationships in facets | Lineage assembled from Job/Dataset observations | Job at the emitter's chosen granularity | Dataset | Input and output Datasets | Namespace/data-source metadata; no execution connection manager | Facets extend Job, Run, and Dataset metadata | Namespace and facets; no deployment environment entity |

### 3.2 Runtime and lifecycle

| Platform | Schedule / trigger | Runtime execution | Retry / attempt | Logs / events / metrics | Artifact | Versioning | Deployment / promotion | Lineage support |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| SSIS | Typically external scheduling such as SQL Server Agent; package execution can also be invoked directly | SSISDB operation/execution and executable-level events | No universal package-attempt abstraction; retry can be orchestrated externally or modeled in a package | Catalog operation messages, events, executable statistics, and logs | DTSX package; ISPAC project deployment file | SSISDB retains project versions in project deployment scenarios; source-control versioning is external | Project or package deployment; environment references/configuration | Design/runtime metadata can expose dependencies, but no universal asset-lineage contract |
| Azure Data Factory | Schedule, tumbling-window, storage-event, and custom-event triggers; manual invocation | Pipeline run and activity runs | Activity retry policy; reruns create or relate runtime observations according to provider behavior | Monitor views, diagnostic logs, activity output, metrics | JSON definitions, templates, and data-flow definitions | Git-backed collaboration may version definitions; live factory is distinct | Publish/deploy through supported lifecycle tooling and external CI/CD | Can integrate with Microsoft data-governance services; provider semantics apply |
| Microsoft Fabric Data Factory | Scheduled and event-based invocation depending on item capability | Pipeline/item run and activity observations | Activity retry configuration where supported | Monitoring hub, run details, output, and diagnostics | Fabric item definitions and deployment packages/metadata | Git integration and item revisions where supported | Workspace/deployment-pipeline and Git-based processes | Platform lineage can relate supported Fabric items and data |
| IBM DataStage | Schedules and orchestration pipelines | Job/flow run with run identifiers | Platform/orchestration behavior; no cross-platform attempt entity established here | Job logs, run status, metrics, and audit events | Flow/job assets and export/deployment packages | Asset/project history depends on product edition and repository lifecycle | Export/import or platform deployment spaces/packages | Catalog and platform services can capture technical lineage |
| Informatica Cloud Data Integration | Schedules, taskflows, and API/manual starts | Job/taskflow run with job/run identifiers | Taskflow and task-specific retry/recovery behavior | Monitor status, row counts, duration, error details, and logs | Export packages and platform assets | Asset versions/source-control features vary by service and configuration | Export/import and platform migration/deployment mechanisms | Metadata/lineage services can describe mappings and data movement |
| AWS Glue | On-demand, scheduled, conditional, and event-driven triggers | Workflow run and job run | Job retry configuration and distinct job runs | CloudWatch logs/metrics, job-run status, workflow graph state | ETL script and job/workflow definitions | Job definition/script versions are managed through platform or external source processes | API/IaC and cross-environment delivery are external lifecycle concerns | Data Catalog and integrations can support lineage; not a single job-domain contract |
| Google Cloud Data Fusion | On-demand, schedule, and upstream/downstream triggers | Pipeline run backed by underlying execution services | Runtime/provider behavior; no universal attempt mapping established here | Run history, logs, metrics, and node-level statistics | Pipeline configuration/export and plugin artifacts | Pipeline versions and Git integration are available in supported workflows | Draft-to-deployed lifecycle and external delivery across instances | Platform can capture dataset-level and field-level lineage for supported pipelines |
| Pentaho Data Integration | Server schedules or external invocation | Transformation/job execution | Job entries and orchestration can model recovery; no universal attempt entity | Logging channels, step metrics, and execution history | KTR transformation and KJB job files | Repository revisions or external source control | Publish/import and server/repository deployment | Pentaho Data Catalog can display lineage; a portable PDI definition/runtime lineage mapping is not established in this research |
| Apache Airflow | DAG schedule, timetable, manual/API, asset/event mechanisms | DAG Run and Task Instance | Task Instance `try_number`, retries, and reschedules | Task logs, scheduler events, callbacks, and metrics | DAG/source files and provider packages | Airflow 3 supports DAG-bundle version tracking for versioned bundle backends; external source revisions can supply provenance, while local bundles may remain unversioned | Deployment of DAGs/config/providers is installation-specific | Inlets/outlets, assets, and lineage integrations are available but not universal to every DAG |
| Dagster | Schedules, sensors, and manual launches | Run with step/asset materialization events | Op/step retry policies and run re-execution | Structured event log, logs, run status, and asset materializations | Code definitions plus materialization metadata | Code version and data version can participate in asset staleness/caching | Code locations/deployments are product/deployment specific | Asset graph and materialization events make lineage prominent |
| Databricks Lakeflow Jobs | Scheduled, file-arrival, continuous, table-update, or other supported triggers | Job run and task runs | Task retry policy and repair/rerun behavior | Run output, logs, task status, duration, and metrics | Notebook, wheel/JAR, SQL, pipeline, or Git-backed source depending on task | Git commit/reference or workspace/release tooling depending on source | Bundles/APIs/UI and workspace delivery processes | Unity Catalog lineage applies to supported workloads/assets |
| dbt | CLI invocation or external/dbt platform job scheduling | Invocation/job run and per-node results | Job retry/rerun behavior is execution-platform specific | Logs, node status/timing, `run_results.json`, and platform run history | `manifest.json`, catalog, run results, compiled SQL, and packages | Project Git revision plus versioned artifact schemas; model versions are a separate dbt concept | Deployment jobs/environments and external CI/CD | Manifest dependencies and catalog metadata describe model/source lineage |
| SQLMesh | Built-in or external scheduling after plan application | Plan execution, model evaluation, and interval state | Restatement/backfill and scheduler recovery do not imply one portable attempt model | Console/log output, audits, state, and interval progress | Model definitions, snapshots, plan/state metadata | Snapshot fingerprints identify definition/dependency changes | Plan creation/application promotes changes into named environments | Model graph and column lineage are derived from SQL where supported |
| Airbyte | Manual/API and configured sync schedules | Sync job | A job may have attempts; exact retry semantics are platform-managed | Job status, logs, records/bytes, and connection health | Configured catalog/state and connector images are operational artifacts, not one pipeline build artifact | Connector/config changes have distinct lifecycles; no universal pipeline version | Connection configuration is deployed in a workspace; enterprise promotion is external | Stream/source-destination relationships are available; broader lineage depends on integrations |
| Fivetran | Managed sync frequency and manual invocation | Connection sync | Service-managed retry/recovery; user-visible sync history rather than a portable attempt contract | Sync history, status, duration, volume, and platform logs | Connector configuration and metadata; no general build artifact | Configuration/schema evolution and connector releases are distinct | Connections can be configured by UI/API/IaC; promotion semantics are external | Platform metadata can expose source-to-destination and transformation lineage |
| Apache NiFi | Processor scheduling, events, and back-pressure-driven flow | Long-running processor/flow activity rather than only bounded pipeline runs | Retry routes, penalization/yield, queues, and back pressure are flow semantics | Bulletins, logs, metrics, counters, and data provenance events | Flow definition/versioned flow | NiFi Registry or source-controlled flow definitions can version flows | Import/version-control and parameterized promotion across instances | Data provenance can trace FlowFiles through processors and relationships |
| OpenMetadata | Metadata ingestion workflows may be scheduled, but the cataloged Pipeline entity is observational | Pipeline status/task status can be ingested from an external platform | Represents external observations; does not establish provider retry semantics | Pipeline status, task status, ownership, tags, and catalog metadata | Ingestion configuration and catalog metadata | Entity/version history concerns metadata, not executable definition authority | Metadata-service deployment is separate from pipeline deployment | Core catalog capability links entities and can display pipeline/data lineage |
| OpenLineage | Emitters publish design-time or runtime events; scheduling is outside the specification | `Run` observed through `RunEvent`; `JobEvent` and `DatasetEvent` can be design-time | Attempts may be represented by emitter conventions/facets; the core does not prescribe provider retry policy | Events and extensible facets carry run/job/dataset observations | No build artifact concept; source-location/version data can be facets | Job/Dataset metadata and source-code version facets, not a deployment version model | Outside the lineage specification | Primary purpose: runtime and design lineage among Jobs and Datasets |

### Matrix observations

- **Research observation:** `Pipeline` is common but not universal. SSIS emphasizes Package, dbt emphasizes project resources and models, Airbyte and Fivetran emphasize Connection, and NiFi emphasizes an operating dataflow.
- **Research observation:** A graph is common but has different semantics: dependency DAG, data transformation graph, routing network with queues, asset graph, or lineage graph.
- **Research observation:** An inner executable unit is not uniform. A task, activity, stage, processor, model, stream sync, and asset materialization are not interchangeable merely because each can appear below a top-level definition.
- **Research observation:** Runtime identity varies by platform and granularity. FlowPlane research must preserve native run identifiers and avoid assuming every provider exposes attempts, node runs, or bounded runs.
- **Research observation:** Versioning and deployment are often external, layered, or provider-specific. A Git commit, package build, provider revision, deployment, and runtime snapshot answer different questions.

## 4. OpenMetadata and OpenLineage comparison

### OpenMetadata

**Documented external behavior:** OpenMetadata is catalog and metadata oriented. Its schema can represent a Pipeline associated with a pipeline service, tasks, schedule information, ownership, tags, source URLs, and status. Its broader catalog supports discovery and relationships among data, pipeline, dashboard, and other entities, while lineage APIs and UI represent upstream/downstream relationships. Pipeline connectors ingest metadata from execution platforms; OpenMetadata is not the workload runtime for those external pipelines.

**Research usefulness:** OpenMetadata demonstrates how ownership, tags, discovery, descriptions, service identity, task metadata, status, and lineage can be projected into a catalog. Those concerns could inform later FlowPlane catalog integration without making the catalog authoritative for FlowPlane orchestration.

### OpenLineage

**Documented external behavior:** OpenLineage centers on `Job`, `Run`, and `Dataset`. A Job represents a defined process that consumes or produces Datasets, a Run is one occurrence of a Job, and facets extend Job, Run, input Dataset, and output Dataset metadata. `RunEvent` records runtime state observations; `JobEvent` and `DatasetEvent` can communicate design-time metadata. Source-code location/version and other metadata can be carried in facets.

**Research usefulness:** OpenLineage provides a valuable interoperability target for runtime lineage and provenance. Its deliberately broad Job granularity and extensible facets help emitters describe heterogeneous systems, but they do not define FlowPlane ownership, orchestration policy, provider contracts, or the complete execution state machine.

**Candidate direction:** FlowPlane may integrate with OpenMetadata and OpenLineage later. Its domain model must not simply copy either schema: OpenMetadata is optimized for catalog concerns, while OpenLineage is optimized for interoperable lineage observations.

## 5. Candidate FlowPlane canonical concepts

All concepts in this section are candidates. Classification indicates research confidence, not architectural approval.

| Candidate | Candidate definition | Why it may belong in FlowPlane | Example native mappings | Current classification |
| --- | --- | --- | --- | --- |
| `ProviderType` | A kind of data-pipeline provider and its recognized behavior/capabilities. | Distinguishes SSIS from future platform types without making one provider the core vocabulary. | SSIS, Airflow, dbt, NiFi provider kinds. | Strong core candidate |
| `ProviderInstance` | A configured, addressable installation or service instance of a provider type. | FlowPlane needs to distinguish multiple external environments and retain provider context. | SSIS catalog/server, Airflow deployment, Data Fusion instance, Airbyte workspace/service context. | Strong core candidate |
| `Pipeline` | A candidate FlowPlane identity for a top-level unit of data-pipeline work, whether referenced or eventually managed. | A stable product identity may be needed above native definitions and versions. The term and ownership semantics remain unsettled. | SSIS package, Airflow DAG, ADF pipeline, dbt selection/job, Airbyte connection, NiFi flow. | Unresolved |
| `PipelineVersion` | An immutable FlowPlane identity representing a particular definition/version boundary for a Pipeline, optionally linked to provider-native definitions, artifacts, and source provenance. | Distinguishes a FlowPlane version boundary from any one provider revision, artifact, or source-control revision without defining final immutability or ownership semantics. | Possible mappings include an SSIS project/package version or ISPAC identity, an Airflow DAG-bundle version, dbt source revision and manifest provenance, or a SQLMesh snapshot fingerprint. | Unresolved |
| `NativeDefinitionReference` | A provider-qualified reference to the authoritative native definition. | Provider-native assets must remain identifiable without copying their full model into the core. | SSIS catalog project/package path, Airflow DAG ID, ADF pipeline name/resource ID, dbt project/resource selection. | Strong core candidate |
| `ResourceReference` | A provider-independent reference to an external system or resource required by a pipeline definition or execution, without assuming the provider-native connectivity model. | Makes an external dependency addressable while leaving its native configuration and semantics with the appropriate provider. | SSIS Connection Manager, Azure Data Factory Linked Service, Airflow Connection, Dagster Resource, or dbt profile/target. These native concepts are not semantically identical. | Unresolved |
| `SecretReference` | A reference to protected credential material resolved through an external or controlled secret mechanism. | Keeps credential indirection distinct from resource configuration. It is not the secret value and does not select a secret-management product. | A provider credential reference, environment secret reference, or external secret identifier; exact mappings remain subject to the later security architecture. | Strong core candidate |
| `Run` | FlowPlane's provider-independent record of requested or observed pipeline execution. | Scheduling, cancellation, status, history, audit, and correlation need a durable FlowPlane identity. | SSIS execution, Airflow DAG Run, Glue job/workflow run, Airbyte sync job. | Strong core candidate |
| `RunAttempt` | A distinct attempt within a Run when provider or FlowPlane whole-run retry semantics warrant it. | Separates logical intent from retries where that distinction exists. Granularity remains unresolved, and an attempt must not be fabricated for providers without such semantics. | Airbyte job attempts and provider-specific whole-run retry/rerun records where explicitly grouped. Airflow task tries belong to TaskInstances, and Tekton TaskRuns are inner-unit executions rather than evidence of a top-level RunAttempt. | Optional/common capability |
| `NativeRunReference` | Provider-qualified identity linking a FlowPlane Run or attempt to native runtime records. | Required for correlation, diagnostics, cancellation, recovery, and reconciliation. | SSIS execution ID, Airflow DagRun ID, ADF pipeline-run ID, Databricks job-run ID. | Strong core candidate |
| `ExecutionContext` | Snapshot/reference set describing the effective provider instance, parameters, environment, identity, definition/version, and invocation context for a Run. | Runtime interpretation and audit require more than a status and timestamps. Exact contents and immutability rules remain unresolved. | SSIS parameter/environment bindings, Airflow logical date and params, dbt target/revision, SQLMesh environment/plan context. | Unresolved |
| `Schedule` | FlowPlane-managed recurring invocation policy for a Pipeline or version/reference. | The MVP includes basic recurring scheduling, while provider-native schedules may also exist. | FlowPlane recurrence versus Airflow DAG schedule, ADF trigger, Fivetran sync frequency. | Strong core candidate |
| `Trigger` | A cause or rule that initiates a Run, possibly manual, scheduled, event-based, or provider-native. | Helps distinguish policy from individual execution requests, but advanced trigger types are outside the MVP. | Manual request, ADF event trigger, Dagster sensor, Glue trigger. | Optional/common capability |
| `Environment` | A named lifecycle or execution context used to vary deployment, configuration, data access, or promotion. | Many platforms have environment-like concepts, but their meanings differ significantly. | SSISDB environment, dbt target/deployment environment, SQLMesh environment, Fabric workspace/stage. | Unresolved |
| `Parameter` | Named input/configuration whose value may be bound at definition, deployment, schedule, or execution time. | Parameterization recurs across providers, but typing, scope, secrecy, and precedence vary. | SSIS project/package parameter, ADF pipeline parameter, Tekton param, dbt variable. | Optional/common capability |
| `DataAsset` | A reference to domain data consumed, produced, or materially transformed by pipeline work. | Supports impact analysis, discovery, and lineage without treating runtime artifacts as data products. | Database table, object-store path, Airbyte stream, dbt model output, Dagster asset. | Unresolved |
| `Artifact` | A definition, build, validation, or runtime artifact associated with pipeline mechanics. | Version, release, deployment, diagnostics, and provenance may need to identify concrete files/bundles/reports. | DTSX, ISPAC, dbt manifest, compiled bundle, validation report. | Optional/common capability |
| `RunEvent` | A timestamped structured observation about a Run or unit within it. | Preserves lifecycle and provider observations without reducing all details to the current state. | SSIS catalog event/message, OpenLineage RunEvent, Dagster event-log record, NiFi provenance event. | Optional/common capability |
| `RunLog` | A reference or record for human-oriented execution log output. | Operators need diagnostics, but ownership, retention, redaction, and live-query behavior vary. | SSIS messages, Airflow task logs, Glue CloudWatch logs, dbt logs. | Optional/common capability |
| `Metric` | A named numeric or structured measurement associated with a run, node, or provider resource. | Duration, row counts, bytes, queue depth, and health can support monitoring without pretending they share identical semantics. | SSIS data-flow statistics, ADF activity metrics, Fivetran sync volume, NiFi queue/processor metrics. | Optional/common capability |
| `Graph` | A provider-independent visualization or relationship projection with declared semantics. | Many providers can expose useful structure, but not every Pipeline requires one canonical graph. | SSIS Control Flow, Airflow DAG, dbt dependency graph, NiFi flow. | Optional/common capability |
| `GraphNode` | A node in a specific Graph projection, carrying stable projection identity and provider detail as needed. | Enables visualization while avoiding a universal executable-unit claim. | SSIS task/container, ADF activity, dbt model/test, NiFi processor/process group. | Optional/common capability |
| `GraphEdge` | A typed relationship in a Graph projection. | Dependency, precedence, routing, and lineage edges require explicit semantics rather than one untyped link. | SSIS precedence constraint, Airflow dependency, NiFi relationship/connection, dbt dependency. | Optional/common capability |
| `UnitRun` / `NodeRun` | A runtime observation correlated to a provider-native inner unit or visual node. | Supports task-level monitoring where the provider exposes suitable identities and states. There may be no universal unit. | SSIS executable event, Airflow TaskInstance, ADF activity run, Databricks task run. | Optional/common capability |
| `Deployment` | A recorded act/result of making a definition, version, or artifact available in a target context. | Provider integration may need deployment visibility, but cross-provider semantics and FlowPlane ownership are unresolved. | SSIS ISPAC deployment, Data Fusion deploy action, SQLMesh plan application, workspace delivery. | Unresolved |

`Connection` should not be used as a generic FlowPlane concept. SSIS, Azure Data Factory, Airflow, and Dagster use their native resource/connectivity concepts differently, while Airbyte and Fivetran use Connection for a substantially different source-to-destination synchronization definition.

`ResourceReference` must not contain raw credentials. Protected credential material is referenced separately through `SecretReference`, which is not the secret value. `SecretIntegration` remains a separate integration family, and selection of a secret-management product remains outside this research.

**Research observation:** The strongest candidates are identities and correlation concepts at the provider, definition-reference, and run boundaries. Concepts below the top-level definition, and lifecycle concepts such as Environment and Deployment, show substantially more semantic variation.

## 6. Provider-specific concepts

The following native concepts should not automatically become FlowPlane core entities:

- **SSIS:** Project, package-specific hierarchy, Control Flow, Container, Precedence Constraint, Data Flow, and Connection Manager.
- **Airflow:** Operator, Sensor, and TaskGroup.
- **NiFi:** Processor, FlowFile, Process Group, Relationship, and Connection queue.
- **dbt:** Model, Seed, Snapshot, Test, and Exposure.
- **SQLMesh:** Snapshot, Plan, and Change category.
- **Azure Data Factory:** Activity, Linked Service, Dataset, and Integration Runtime.
- **Fabric Data Factory:** Activity, Connection, inline source/destination properties, and gateway concepts; these are not aliases for ADF Dataset, Linked Service, or Integration Runtime entities.
- **Dagster:** Asset, Op, Resource, and Partition.
- **Airbyte / Fivetran:** Connection, Stream, and sync-specific concepts.

These concepts remain important inside provider adapters, provider extensions, native-detail views, and capability-specific projections. Excluding them from an assumed universal core does not imply hiding or discarding them.

Identical words can be misleading:

- A **connection** can mean credentials/connectivity to an external service, an Airbyte/Fivetran source-to-destination replication configuration, or a NiFi edge with a queue.
- A **task** can mean an SSIS executable, Airflow Task/TaskInstance definition-runtime pair, ADF activity-like work, a DataStage task node, or catalog metadata with no execution authority.
- A **project** can package deployable SSIS artifacts, organize Informatica assets, contain dbt source files, or merely group work in a platform UI.
- An **environment** can bind SSIS parameter values, select a dbt target, represent an isolated SQLMesh model state, identify a CI deployment target, or informally name an installation.

**Recommendation:** Future canonical vocabulary should define semantics and authority explicitly, then map provider terms to those semantics. Word similarity alone is insufficient evidence for a common entity.

## 7. DataAsset versus Artifact

### Candidate `DataAsset`

A `DataAsset` represents domain data consumed or produced by pipeline work. Examples include:

- a database table or view;
- a file or dataset;
- an object-store object or path;
- a stream or topic;
- a dbt model output;
- a Dagster asset.

An asset may be physically stored, logical, partitioned, versioned by its data platform, or referenced through a metadata system. FlowPlane might initially retain lightweight references rather than owning a catalog record.

### Candidate `Artifact`

An `Artifact` represents a definition, build, validation, or runtime output associated with pipeline mechanics. Examples include:

- a DTSX package;
- an ISPAC deployment bundle;
- a dbt manifest;
- a compiled bundle;
- a validation report;
- a build package.

**Research observation:** The distinction is about role, not merely file format. A CSV containing business data is plausibly a DataAsset; a JSON manifest describing executable definitions is plausibly an Artifact. Some provider objects can be ambiguous or serve both roles in different contexts.

**Open question:** The exact final boundary between DataAsset and Artifact, including ownership, identity, retention, and version semantics, requires later review.

## 8. Definition-time versus runtime concepts

Definition-time concepts describe intended work and its references:

- `Pipeline`;
- definition version or native reference;
- `Graph`;
- `Parameter` declarations;
- resources/connections;
- `DataAsset` inputs and outputs.

Runtime concepts describe a particular request, attempt, or observation:

- `Run`;
- `RunAttempt`;
- `NativeRunReference`;
- `ExecutionContext`;
- `RunEvent`;
- `RunLog`;
- `Metric`.

**Research observation:** Mixing these layers creates ambiguity. An Airflow Task is a definition while a TaskInstance is runtime state; an SSIS package is a definition while its catalog execution and executable events are runtime observations; a dbt manifest is a definition artifact while `run_results.json` describes an invocation. A future FlowPlane model should preserve that separation even when provider APIs return both together.

## 9. Visualization projection

The candidate separation for visualization is:

```text
Provider-native representation
        |
        v
Provider adapter
        |
        v
FlowPlane visualization representation
        |
        v
React Flow
```

Visualization is a projection. React Flow is a presentation/editor technology, not the canonical backend domain model. `Graph`, `GraphNode`, and `GraphEdge` may be an optional provider capability rather than mandatory internals of every `Pipeline`.

The current MVP example is an SSIS Control Flow projection for read-only monitoring and exploration. Tasks, containers, and precedence constraints can become view nodes/edges; provider runtime observations can overlay status, duration, warnings/errors, and relevant logs. That projection does not turn SSIS Control Flow or React Flow serialization into the universal FlowPlane workflow model, and it does not imply MVP package authoring or Data Flow design.

## 10. Lineage projection

The candidate lineage relationship is:

```text
Pipeline / Run observations
        |
        v
Lineage projection
        |
        v
DataAsset inputs / outputs
        |
        v
Potential OpenLineage integration
```

The projection could combine declared definition-time dependencies with observed runtime inputs/outputs, while retaining provenance about which provider and observation produced each relationship.

OpenLineage is a potential interoperability target, not the FlowPlane domain schema. A FlowPlane Run may need orchestration state, control authority, native correlation, audit data, or retry semantics that an OpenLineage event does not own. Conversely, OpenLineage facets may describe dataset metadata and lineage details that should remain an integration projection rather than mandatory Run fields.

## 11. Source control and versioning concepts

GitHub, GitLab, Forgejo, Gitea, and generic Git repositories are external source-control systems. They are not data-pipeline providers, and FlowPlane should not become a Git hosting server.

Candidate concepts for later evaluation are:

| Candidate | Candidate meaning |
| --- | --- |
| `SourceRepository` | Reference to an external repository and source-control integration context. |
| `SourceRevision` | Immutable source revision identity, commonly a Git commit SHA. |
| `SourceReference` | Potentially movable human-oriented reference such as a branch or tag, plus an optional path. |
| `PipelineVersion` | FlowPlane identity for an immutable or controlled definition/version boundary; not necessarily identical to a Git commit. |
| `SourceProvenance` | Evidence linking a version/artifact to repository, revision, reference, path, checksums, and producing process where available. |

**Candidate relationship:** A `PipelineVersion` may optionally reference a repository, commit SHA, branch or tag, source path, and native artifact checksum. That information supports traceability without requiring the provider itself to be Git-native.

**Research observation:** A single Git commit can contain many pipeline definitions; one pipeline version can be built from multiple repositories or generated inputs; an unchanged commit can be built with different dependencies; and provider-native definitions may live only in a service/catalog. Therefore `PipelineVersion = Git commit` is not a safe universal assumption.

Providers that are not Git-native must remain supported. The MVP's provider-native SSIS discovery/reference mode does not require Git or CI/CD.

## 12. CI/CD and delivery concepts

GitHub Actions, GitLab CI, Forgejo Actions, Jenkins, Tekton, and other appropriate external CI/CD systems define their own workflows, jobs/tasks, runners/agents, pipeline runs/builds, artifacts, environments, approvals, and deployment mechanisms. They are delivery integrations, not data-pipeline providers.

Candidate delivery concepts for later evaluation are:

- `Build` — an external or observed process that validates, tests, compiles, or packages source;
- `BuildArtifact` — an immutable or content-addressed output produced by a Build;
- `Release` — an identified set of approved versions/artifacts intended for delivery;
- `Deployment` — a recorded delivery of a version/artifact to a target Environment;
- `Promotion` — movement or authorization of a version/release between lifecycle contexts;
- `Approval` — an authorization record or external approval reference;
- `Environment` — the target context whose exact cross-provider semantics remain unresolved;
- `Validation` — a check/result associated with source, definition, artifact, or deployment.

External CI may perform:

```text
source
  -> validate
  -> test
  -> build/package
  -> artifact
```

FlowPlane may later coordinate:

```text
artifact/version
  -> release
  -> deploy
  -> promote
  -> environment
```

**Candidate direction:** FlowPlane should integrate with external CI/CD systems rather than implement a complete CI engine. This research does not decide the integration protocol, event model, artifact store, approval model, or ownership boundary for delivery operations.

## 13. Progressive adoption modes

These are candidate operating modes for progressive adoption, not three separate FlowPlane products.

### Mode 1 — Provider-native

Existing provider assets are discovered or referenced directly:

```text
Existing SSIS package
        |
        v
FlowPlane reference
```

No Git or CI/CD integration is required. This mode supports simple installations and MVP adoption.

### Mode 2 — Git-linked

Pipeline definitions or related source are linked to an external Git repository. FlowPlane records available provenance such as:

```text
repository
commit
tag/branch
path
```

Git remains external. The link does not make the repository, commit, or file automatically authoritative for every provider-native definition.

### Mode 3 — Git + CI/CD integrated

An enterprise lifecycle may take this form:

```text
Developer
    |
    v
Git
    |
    v
review / pull request
    |
    v
external CI
    |
    v
validation / tests / build
    |
    v
artifact
    |
    v
FlowPlane version/release
    |
    v
DEV -> UAT -> PROD
```

FlowPlane may coordinate pipeline lifecycle and deployment where appropriate, but it does not become a Git forge or CI execution engine.

**Candidate adoption principle:** A simple installation must not require enterprise source-control, CI/CD, release, approval, or promotion integrations.

## 14. Integration families

The following taxonomy is conceptual only. The names do not define interfaces, C# contracts, plugins, processes, or deployment boundaries.

| Integration family | Distinct semantic role | Examples |
| --- | --- | --- |
| `DataPipelineProvider` | Discover, reference, execute, observe, and possibly deploy work in an external data-pipeline platform. | SSIS first; future candidates may include Airflow, dbt, and other suitable platforms. |
| `SourceControlIntegration` | Link external repositories, revisions, references, reviews, and source provenance. | GitHub, GitLab, Forgejo, Gitea, generic Git. |
| `CICDIntegration` | Observe or coordinate external validation, build, artifact, release, approval, and deployment workflows. | GitHub Actions, GitLab CI, Forgejo Actions, Jenkins, Tekton. |
| `MetadataCatalogIntegration` | Publish or consume catalog metadata, ownership, tags, discovery information, and entity references. | OpenMetadata and other catalogs. |
| `LineageIntegration` | Emit, ingest, or correlate definition-time and runtime lineage observations. | OpenLineage and catalog lineage APIs. |
| `SecretIntegration` | Resolve or broker protected configuration without making provider credentials ordinary domain fields. | External secret managers; no product selected here. |
| `ObservabilityIntegration` | Export or associate logs, metrics, traces, alerts, and operational telemetry. | External observability systems; no product selected here. |

**Research observation:** These families have different authority, identity, lifecycle, security, and failure semantics. Forcing them through one universal “provider” interface would conflate workload execution with source hosting, delivery automation, metadata, lineage, secret resolution, and telemetry.

## 15. Concept relationship model

The following is a candidate relationship model, not accepted architecture:

```text
ProviderType
|
ProviderInstance
|
Pipeline
+-- NativeDefinitionReference
+-- PipelineVersion
+-- Schedule*
+-- Graph*
+-- consumes/produces --> DataAsset*
|
v
Run
+-- ExecutionContext
+-- RunAttempt*
+-- NativeRunReference
+-- RunEvent*
+-- RunLog*
+-- Metric*

SourceRepository*
|
SourceRevision*
|
PipelineVersion

PipelineVersion / Build* / Run
|
+-- Artifact*

PipelineVersion / Artifact
|
Release*
|
Deployment*
|
Environment
```

`*` means optional or capability-dependent. Cardinalities, ownership, identifiers, lifecycle states, and persistence are intentionally unspecified.

## 16. Open domain questions

- Is `Pipeline` the correct universal term?
- Does FlowPlane own a Pipeline definition or reference provider-native definitions?
- Can a Pipeline later be FlowPlane-managed as well as provider-native?
- Is there a universal inner executable unit?
- Should Graph be optional rather than fundamental?
- What are the semantics of Environment?
- How do a FlowPlane Schedule and provider-native schedules coexist?
- Is DataAsset a core entity or a lightweight reference?
- How should native resource hierarchies be represented?
- What is the final semantic boundary of ResourceReference?
- Which resource configuration belongs in FlowPlane versus provider-native systems?
- How are ResourceReference and SecretReference related?
- Which system is authoritative for credentials and secret rotation?
- What is the final boundary between DataAsset and Artifact?
- What makes a PipelineVersion immutable?
- How do Git revisions relate to PipelineVersion?
- Is Release a FlowPlane core concept?
- What deployment and promotion semantics genuinely work across providers?
- Which concepts belong to data-pipeline providers versus other integration families?
- How should OpenMetadata and OpenLineage integrations interact with FlowPlane ownership?
- Is `RunAttempt` a FlowPlane-owned retry attempt, a provider-native attempt, or a typed relationship that can represent either?
- Which definition and runtime fields are authoritative in FlowPlane, provider systems, source control, CI/CD, or metadata systems?
- How are provider-native schedules discovered, displayed, disabled, or reconciled without claiming FlowPlane owns them?
- When a provider exposes multiple graphs (control, data, asset, and lineage), how are projection type and edge semantics identified?
- Can bounded-run concepts represent long-running NiFi flows and continuous jobs without distortion?
- Which capabilities are declared by provider type, provider instance, definition, or runtime environment?

## 17. Implications for ADR-0002

This research suggests that ADR-0002 should evaluate, rather than assume, the following principles:

- a small provider-independent core centered on stable identity, intent, runtime correlation, and authority boundaries;
- capability-based extensions for graphs, inner-unit observations, lineage, assets, deployment, parameters, logs, metrics, and other non-universal behavior;
- preservation of provider-native identities, hierarchies, terminology, diagnostics, and execution semantics;
- separation of canonical orchestration concepts from visualization and lineage projections;
- separation of definition-time concepts from runtime observations;
- separate integration families for data-pipeline providers, source control, CI/CD, metadata catalogs, lineage, secrets, and observability;
- progressive adoption that supports provider-native references before optional Git and CI/CD integration.

ADR-0002 should not infer a universal task model merely from recurring labels, and it should not define an interface until the minimum core responsibilities and optional capability boundaries have been reviewed. This document supplies research inputs only; it does not create ADR-0002 or decide its outcome.

## References

All external references below are first-party project or vendor documentation. They support descriptions of external systems, not the candidate FlowPlane classifications.

### Data integration / ETL

- Microsoft, [Integration Services (SSIS) packages](https://learn.microsoft.com/en-us/sql/integration-services/integration-services-ssis-packages?view=sql-server-ver17), [Control Flow](https://learn.microsoft.com/en-us/sql/integration-services/control-flow/control-flow?view=sql-server-ver17), and [Deploy Integration Services projects and packages](https://learn.microsoft.com/en-us/sql/integration-services/packages/deploy-integration-services-ssis-projects-and-packages?view=sql-server-ver17).
- Microsoft, [Introduction to Azure Data Factory](https://learn.microsoft.com/en-us/azure/data-factory/introduction), [Pipelines and activities](https://learn.microsoft.com/en-us/azure/data-factory/concepts-pipelines-activities), and [Integration Runtime](https://learn.microsoft.com/en-us/azure/data-factory/concepts-integration-runtime).
- Microsoft, [Fabric Data Factory overview](https://learn.microsoft.com/en-us/fabric/data-factory/data-factory-overview), [Differences between Data Factory in Fabric and Azure](https://learn.microsoft.com/en-us/fabric/data-factory/compare-fabric-data-factory-and-azure-data-factory), [Data Factory pipeline CI/CD](https://learn.microsoft.com/en-us/fabric/data-factory/cicd-pipelines), and [Monitor Data Factory](https://learn.microsoft.com/en-us/fabric/data-factory/monitor-data-factory).
- IBM, [DataStage stages](https://www.ibm.com/docs/en/ws-and-kc?topic=flows-datastage-stages), [Orchestrating DataStage flows](https://www.ibm.com/docs/en/ws-and-kc?topic=datastage-orchestrating-flows-orchestration-pipelines), and [Running and scheduling pipelines](https://www.ibm.com/docs/en/ws-and-kc?topic=pipelines-running-saving).
- Informatica, [Asset migration](https://docs.informatica.com/integration-cloud/cloud-data-integration/current-version/asset-management/asset-migration.html), [Viewing connection dependencies](https://docs.informatica.com/integration-cloud/cloud-data-integration/current-version/data-integration-connections/connection-configuration/viewing-connection-dependencies.html), and [Monitoring taskflow status](https://docs.informatica.com/integration-cloud/cloud-data-integration/current-version/taskflows/taskflows/monitoring-taskflow-status-with-the-status-resource.html).
- AWS, [AWS Glue concepts](https://docs.aws.amazon.com/glue/latest/dg/components-key-concepts.html), [Workflows](https://docs.aws.amazon.com/glue/latest/dg/workflows_overview.html), and [Viewing job runs](https://docs.aws.amazon.com/glue/latest/dg/view-job-runs.html).
- Google Cloud, [Cloud Data Fusion concepts](https://docs.cloud.google.com/data-fusion/docs/concepts/overview), [Studio overview](https://docs.cloud.google.com/data-fusion/docs/concepts/studio-overview), and [Deploy and run pipelines](https://docs.cloud.google.com/data-fusion/docs/concepts/deploy-and-run-pipelines).
- Pentaho, [Basic concepts of Pentaho Data Integration](https://docs.pentaho.com/pdia-data-integration/basic-concepts-of-pdi), [Working with transformations](https://docs.pentaho.com/pdia-data-integration/pipeline-designer/working-with-transformations), [Logging and performance monitoring](https://docs.pentaho.com/pdia-data-integration/9.3-data-integration/data-integration-perspective-in-the-pdi-client/logging-and-performance-monitoring), and [Pentaho Data Catalog lineage](https://docs.pentaho.com/pdc-use/pdc-lineage).

### Workflow orchestration

- Apache Airflow, [Core Concepts](https://airflow.apache.org/docs/apache-airflow/stable/core-concepts/), [DAGs](https://airflow.apache.org/docs/apache-airflow/stable/core-concepts/dags.html), [Tasks](https://airflow.apache.org/docs/apache-airflow/stable/core-concepts/tasks.html), and [DAG bundles](https://airflow.apache.org/docs/apache-airflow/stable/administration-and-deployment/dag-bundles.html).
- Dagster, [Defining assets](https://docs.dagster.io/guides/build/assets/defining-assets), [Asset versioning and caching](https://docs.dagster.io/guides/build/assets/asset-versioning-and-caching), [External resources](https://docs.dagster.io/guides/build/external-resources), and [Logging](https://docs.dagster.io/guides/log-debug/logging).
- Databricks, [Lakeflow Jobs](https://docs.databricks.com/aws/en/jobs), [Configure and edit jobs](https://docs.databricks.com/aws/en/jobs/configure-job), and [Control flow in jobs](https://docs.databricks.com/gcp/en/jobs/control-flow).

### Transformation

- dbt Labs, [Models](https://docs.getdbt.com/docs/build/models), [Sources](https://docs.getdbt.com/docs/build/sources), [dbt artifacts](https://docs.getdbt.com/reference/artifacts/dbt-artifacts), and [Deploy jobs](https://docs.getdbt.com/docs/deploy/deploy-jobs).
- SQLMesh, [Overview](https://sqlmesh.readthedocs.io/en/stable/concepts/overview/), [Snapshots](https://sqlmesh.readthedocs.io/en/stable/concepts/architecture/snapshots/), [Plans](https://sqlmesh.readthedocs.io/en/stable/concepts/plans/), and [Environments](https://sqlmesh.readthedocs.io/en/stable/concepts/environments/).

### Ingestion, replication, and dataflow

- Airbyte, [Workspaces](https://docs.airbyte.com/platform/organizations-workspaces/workspaces), [Configuring schemas and streams](https://docs.airbyte.com/platform/using-airbyte/configuring-schema), and [Airbyte connection and sync operations](https://docs.airbyte.com/platform/airbyte-mcp/tools).
- Fivetran, [Core concepts](https://fivetran.com/docs/core-concepts), [Sync overview](https://fivetran.com/docs/core-concepts/syncoverview), and [Fivetran Platform Connector](https://fivetran.com/docs/logs/fivetran-platform).
- Apache NiFi, [User Guide](https://nifi.apache.org/nifi-docs/user-guide.html) and [Migrating deprecated components and features for NiFi 2.0](https://cwiki.apache.org/confluence/spaces/NIFI/pages/240883792/Migrating%2BDeprecated%2BComponents%2Band%2BFeatures%2Bfor%2B2.0.0).

### Metadata and lineage

- OpenMetadata, [Pipeline entity schema](https://github.com/open-metadata/OpenMetadata/blob/main/openmetadata-spec/src/main/resources/json/schema/entity/data/pipeline.json), [Pipeline connectors](https://docs.open-metadata.org/latest/connectors/pipeline), and [Data lineage](https://docs.open-metadata.org/latest/how-to-guides/data-lineage).
- OpenLineage, [Object Model](https://openlineage.io/docs/next/spec/object-model/), [Facets and extensibility](https://openlineage.io/docs/spec/facets/), and [Job facets](https://openlineage.io/docs/spec/facets/job-facets/).

### Source control

- Git, [Git user manual](https://git-scm.com/docs/user-manual.html) and [git-tag documentation](https://git-scm.com/docs/git-tag).
- GitHub, [About repositories](https://docs.github.com/en/repositories/creating-and-managing-repositories/about-repositories).
- GitLab, [Git basics](https://docs.gitlab.com/topics/git/basics/).
- Forgejo, [User guide](https://forgejo.org/docs/latest/user/).
- Gitea, [Repository documentation](https://docs.gitea.com/usage/repository/).

### CI/CD

- GitHub, [Understanding GitHub Actions](https://docs.github.com/en/actions/get-started/understanding-github-actions) and [Workflow artifacts](https://docs.github.com/en/actions/concepts/workflows-and-actions/workflow-artifacts).
- GitLab, [CI/CD pipelines](https://docs.gitlab.com/ci/pipelines/), [Job artifacts](https://docs.gitlab.com/ci/jobs/job_artifacts/), and [Environments](https://docs.gitlab.com/ci/environments/).
- Forgejo, [Actions overview](https://forgejo.org/docs/latest/user/actions/overview/) and [Actions quick start](https://forgejo.org/docs/latest/user/actions/quick-start/).
- Gitea, [Actions overview](https://docs.gitea.com/usage/actions/overview/).
- Jenkins, [Pipeline](https://www.jenkins.io/doc/book/pipeline/) and [Using a Jenkinsfile](https://www.jenkins.io/doc/book/pipeline/jenkinsfile/).
- Tekton, [Concept model](https://tekton.dev/docs/concepts/concept-model/), [Getting started with Pipelines](https://tekton.dev/docs/getting-started/pipelines/), and [PipelineRuns](https://tekton.dev/docs/pipelines/pipelineruns/).
