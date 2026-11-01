# Milestone B — First Durable Manual SSIS Execution Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` after explicit implementation approval. Follow the task-specific RED/GREEN and independent review gates. This scope amendment authorizes no application code, migration, service installation, fixture deployment or native execution. Do not commit, push or change issue closure state during planning.

**Goal:** Explicitly register one discovered SSIS package, execute one durable manual Run through a securely enrolled, installable Windows Agent using Windows Integrated Authentication, retain native correlation, observe a verified result and recover observation after backend/agent interruption. The service download/install/uninstall, agent health and security acceptance are mandatory; host-observation and advanced Data Flow telemetry are feasibility-only deliverables.

**Architecture:** Provider-independent Domain/Application own registration, immutable execution intent and orchestration. Providers.Ssis owns native identity, revision/profile evidence, public SQL and native observation. The existing backend coordinates a small Windows execution-plane service; only the agent performs the approved SSIS mutations using its authorized Windows service identity. PostgreSQL remains the durable orchestration authority; agent receipts/recovery evidence do not replace Run/Attempt state. No broker, general remote-command runner or monitoring infrastructure is introduced. This SSIS route does not require future providers to use a Windows Agent.

**Tech Stack:** Preserve .NET 10 (SDK floor 10.0.113), EF Core/Npgsql/PostgreSQL 18, Microsoft.Data.SqlClient 7.1.1 only in Providers.Ssis, React 19/TypeScript 6/Vite 8, Keycloak/OIDC and Nginx. Native WSL development and containerized frontend tooling remain unchanged; no dependency upgrade is proposed here.

**Spec:** [MVP implementation plan, Slices 2–4](../mvp-implementation-plan.md#10-slice-2--pipeline-registration), [domain model](../../domain/mvp-domain-model.md), [SSIS technical design](../../architecture/providers/ssis-provider-technical-design.md), and Accepted [ADR-0001](../../adr/0001-control-plane-execution-plane-separation.md), [ADR-0004](../../adr/0004-capability-based-data-pipeline-provider-architecture.md), [ADR-0005](../../adr/0005-execution-lifecycle-authority-and-reconciliation.md), [ADR-0006](../../adr/0006-built-in-secret-storage-and-external-root-key.md). This plan records the approved B-01 decisions and targeted refinements; it does not modify or supersede those ADRs.

**Agent security review:** [Accepted ADR-0007](../../adr/0007-windows-agent-security-transport-and-durable-operations.md) records the reviewed Windows Agent security boundary, pairing, self-signed mTLS, service-token proof and recovery contracts. Accepted status is prepared for the final documentation staging gate under conditional operator approval and independent review; it is not implementation or external-operation approval. It replaces neither B-01 history nor the earlier Accepted ADRs above.

## Status and authority

- Planning baseline verified on 2026-10-28: clean `main` at `c36f92d6601682719f8463d4610263e4f6a5e403`; [Foundation run 40673397391](https://github.com/aaliakbarian/flowplane/actions/runs/40673397391) succeeded for that exact commit.
- Milestone A, parent #1 and all its children remain complete/closed. Preserve their evidence and issue history.
- [Milestone B #2](https://github.com/aaliakbarian/flowplane/milestone/2), [parent #16](https://github.com/aaliakbarian/flowplane/issues/16). B-01 [#17](https://github.com/aaliakbarian/flowplane/issues/17) is **Complete/closed**; preserve its original design/history. Its committed plan baseline is `cdf058a`. B-02 is in progress; B-03–B-11 remain planned. GitHub is the live tracker.
- On 2026-10-31 the controlled SQL-authenticated `Success.dtsx` proof reached `catalog.create_execution` and returned **27123/state 1**, wrapped by the reviewed template as **51042/state 1**. SQL authentication and project READ/EXECUTE were positively verified on `AminLaptop` / `17.0.1000.7`, project 4 / LSN 5. No execution ID was returned; configure/start and terminal outcome were **Not Executed**, not independently proved unsupported. Do not retry or infer/adopt an ID.
- The operator subsequently approved the Windows Agent direction and the scope clarification below. This is an explicit architecture follow-on, not a silent fallback or a rewrite of B-01/B-02 evidence. Existing uncommitted B-02 fixture sources are retained unchanged by this planning amendment. Service implementation/installation, credential/enrollment changes, fixture deployment and native execution still require their own approvals.

## Global constraints

- Discovery never registers Pipelines. Do not alter A-11a complete scanning, A-11b atomic publication, A-12 durable discovery capacity or safe native-checked browsing.
- Native observation is not execution ownership: visible external/unattributed executions never automatically create Runs/Attempts, acquire FlowPlane capacity or become an ambiguous create's correlation.
- Pipeline native binding is immutable. A changed path/provider requires a new Pipeline, not automatic relinking.
- One global active Run, independent of discovery capacity; no unbounded backlog. At most one initial RunAttempt and no automatic retries in B. No exactly-once claim.
- Persist each native-call admission before create/configure/start. Persist the native execution ID before any subsequent native mutation. No PostgreSQL transaction spans SQL Server I/O.
- Preserve B-02's failed SQL-authentication proof. The new execution gate must separately prove Windows Integrated Authentication under the intended least-privileged service identity; an interactive administrator's success or A-10 permission evidence is insufficient. No elevation, internal procedures or unapproved authentication fallback.
- SQL major 17 remains Supported only for the evidenced A-10 validation boundary; 15/16 remain Unverified. B-02/B-11 add narrowly evidenced execution behavior, not blanket runtime/build certification or broader format support.
- Initial execution supplies no project/package parameter values and selects no environment reference. Only the strict self-contained profile below is executable.
- No plaintext credentials, SecretReference locators, connection strings, tokens, private keys, parameter defaults or environment values in execution snapshots, diagnostics, logs or browser artifacts.
- Preserve X.509-protected PostgreSQL Data Protection keys, encrypted managed secrets, backend-only read-only root mount, exact operator authorization, endpoint allowlisting and TLS policy.
- No cancellation endpoint, scheduling, package authoring/deployment, artifact parsing, graphs or historical SSIS version management. The approved Windows service is the sole new execution-plane deployable; no broker, telemetry pipeline, dashboard/alerting framework, universal Host entity or general-purpose agent/plugin system.
- Development acceptance data remain disposable. Production recovery requirements remain separate; never erase/regenerate existing root material. Previously declined standalone cryptographic/log probes remain Not Executed and require fresh authorization.

## Review focus

1. SQL authentication permits metadata but not an execution operation: B-02 proves each operation independently and stops on an authentication blocker.
2. Parameter bindings or relevant metadata change with an unchanged project LSN: B-03/B-07 reject obsolete execution evidence without reading values.
3. A stale worker returns from a previously admitted native call: B-08 permits only monotonic late correlation, never renewed authority or duplicate dispatch.
4. Provider deletion retires discovery before a late FK rejects it: B-04/B-06 reject under the first provider lock, before any retirement side effect.
5. Native status 9, invisible execution or lost acknowledgement looks like completion: B-09 preserves uncertainty and capacity rather than inventing success or settlement.
6. A healthy agent heartbeat is mistaken for host health, SQL-host health or permission to execute: keep freshness/readiness/capability evidence separate.
7. Revocation, agent restart or transport replay creates duplicate SQL work: authenticate/bind/fence every command and preserve uncertain obligations rather than replaying native mutations.

## Approved Windows Agent scope clarification (2026-10-31)

### Mandatory Milestone B product and security acceptance

| Deliverable | Acceptance boundary | Task ownership |
| --- | --- | --- |
| Small Windows Service | Authenticated FlowPlane UI download of a versioned, integrity-verifiable package; reviewed install/uninstall and service-account instructions; installation does not execute SSIS. Uninstall must not silently erase unsettled receipts, Runs or root material. No automatic updater/fleet manager. | Separate Windows Agent foundation child; B-10 operator UI completion; B-11 live acceptance. B-07 owns only the SSIS adapter. |
| Secure lifecycle | Operator-authorized, bounded enrollment; unique durable agent identity and protected private credential; authenticated/encrypted communication; explicit rotation/re-enrollment and revocation. No embedded shared fleet secret, browser password persistence or secret-bearing download/URL/log. | Agent foundation and its persistence review; B-02 actual identity proof; B-06/B-08 Run integration. |
| Native execution | Windows Integrated Authentication under the separately approved service identity, explicit non-Scale-Out/64-bit/null-reference profile and public SSISDB procedures only. No stored Windows password in a ProviderInstance SQL credential or implicit LocalSystem/sysadmin requirement. | B-02 real feasibility; B-07 SSIS adapter/service. |
| Durable commands | Persist Run intent and per-call admissions; bind each command/receipt to agent, provider/source context, Run/Attempt, operation ID and fence; once-set native ID before configure/start; replay acknowledgements without reissuing native work; recover across backend and service crashes. | B-06–B-09. |
| AgentHealth | Connectivity, heartbeat/observed-at freshness, version/protocol compatibility, execution readiness and typed capability availability. Stale/offline/revoked/incompatible is not healthy; heartbeat alone is not successful native execution. | Agent foundation; B-07/B-08 supply execution evidence; B-10 displays it. |
| Security acceptance | Least-privilege service/SQL identity, strict endpoint and typed-command allowlists, enrollment/token expiry and replay resistance, transport peer authentication, credential protection/rotation/revocation, source binding, fencing, redaction and ambiguous-call recovery. | B-02 design checkpoint; B-07/B-08 RED/GREEN; B-11 independent security/live acceptance. |

Execution and AgentHealth are operational capabilities. HostObservations and DataFlowTelemetry are optional, typed/versioned extensions, not mandatory fields on Pipeline/Run or a universal metrics document. Core has no Windows counters, SSIS columns or universal Host hierarchy; SSIS metadata/SQL remains in Providers.Ssis even when hosted by the agent. A capability can be Available, Unavailable or Unknown with freshness/reason, without claiming implementation/evidence that does not exist.

Before agent implementation, apply Accepted ADR-0007 and obtain separate approval of the foundation implementation/schema contracts. The approved direction is outbound bounded HTTPS polling to a dedicated listener in the existing backend, exact-pinned per-agent mTLS, protected local identity and database-current revocation. No persistent channel, backend CA service or Nginx certificate-header authentication. Operator pairing uses the local helper and explicit two-sided confirmation, not manual CSR/fingerprint handling. Keep the existing WSL/.NET/Linux Compose control plane and containerized frontend; do not add a Linux SSIS runtime or require an agent for Milestone A metadata-only use.

The mandatory command surface is closed and typed: resolve/check, create, set the approved system settings, start, observe and report agent health. No caller-supplied SQL/PowerShell/shell text, arbitrary process/file access, service control, package deployment, cancellation or credential impersonation. Host PoC collectors are fixed read-only allowlisted observations, never a remote management channel. Require resource-level operator authorization and endpoint policy in addition to authenticated agent identity; an authenticated but revoked/unassigned agent is not authorized.

Retain PostgreSQL's durable admission/fence as authority. The agent needs bounded durable deduplication/receipt evidence before native calls and after returned IDs, with an explicit acknowledgement back to PostgreSQL before subsequent mutations. Final local journal format, protection, retention and reconciliation are B-06/B-08 design work; do not turn it into a second Run database. Disk loss, reinstall, lost receipts or network partitions cannot justify reissuing an uncertain create/start or releasing capacity. Revocation blocks new authority; it cannot undo an already admitted in-flight SQL call or cancel a running package. No graceful-shutdown dependency or exactly-once claim.

### Final agent security contracts (Accepted ADR-0007)

- Installer/setup generates the protected CNG identity automatically. The operator requests a one-time pairing code in authenticated HTTPS UI and pastes it only into the local setup helper. The service submits its public certificate over server-authenticated HTTPS, proves possession using a pending-only mTLS exchange, and both screens display a matching verification phrase. Only explicit operator confirmation plus proof permits atomic code redemption and exact certificate binding. No credentials in installers, URLs, arguments or browser storage; no manual CSR required.
- Kestrel uses `AllowCertificate` on the dedicated HTTPS listener to permit initial pairing without a certificate, plus a strict exact-pin/profile `ClientCertificateValidation` callback. Only committed pending-proof, active or bounded RetiringReceiptsOnly pins are handshake candidates; pending pins only prove pairing and retiring pins only recover receipts, neither admits native work. The named ASP.NET certificate scheme allows SelfSigned, enforces explicit clientAuth EKU/lifetime and checks current database status. Do not enable certificate-auth result caching or certificate forwarding. Handshake pin snapshots are not revocation authority; every operation and native admission rechecks database versions. An unknown certificate, forged header or same-key/different-DER certificate is not trusted.
- Backend server TLS trust is independent from client pinning and from the existing Data Protection PFX. Local development requires a separately reviewed exact HTTPS origin/SAN/trust bootstrap for both pairing UI and agent, not HTTP fallback or production PKI infrastructure.
- Initial co-located identity candidate is `NT SERVICE\FlowPlaneAgent`; future SCM/SID, Windows SQL login/user and scoped public catalog grants are specified in ADR-0007 but require separate approval. B-02 must prove actual service-token Integrated Authentication and every native operation; heartbeat, interactive execution and project permissions do not establish support. Domain/gMSA/remote SQL remain separate profiles.
- PostgreSQL intent → local acceptance → Core one-shot admission → flushed MayHaveInvoked → SQL once → flushed native result → durable Core correlation precedes the next mutation. Lost permit/result responses never justify native replay. Atomically fence/recover work only when no native admission is proven; admitted unknown work retains capacity. Use ADR-0007's audited ActionRequired runbook, not a force unlock or lease-expiry release.
- Separate foundation from B-07: foundation installs/pairs/authenticates/revokes/rotates and reports truthful AgentHealth with synthetic journal tests; B-07 adds only SSIS-owned typed SQL under the proven service identity. Agent foundation does not create Runs or execute packages.

### HostObservations PoC — evidence required, production monitoring not required

Investigate one bounded read-only sample on the agent's own Windows host: CPU usage over an explicit sampling interval, used/available memory with units, disk used/free/capacity for selected local volumes, selected service states, and the agent process's own health/resource facts. Candidate mechanisms are documented Windows counters/system APIs, WMI read-only properties and ServiceController status reads. Do not invoke service-control/WMI mutation methods, enumerate process arguments/environment, collect arbitrary file paths or require broad admin rights just to obtain optional metrics.

Label agent health, agent-host observations and target SQL-host health separately. Agent-host metrics are not SQL-host metrics unless co-location is positively established; no remote SQL-host monitoring is implied. Use observed-at time, sample duration, source identity, units and per-field availability; denied/unsupported counters mean Unknown/Unavailable, not zero. Keep samples ephemeral/bounded and stop collection independently of execution; no telemetry history table, global Host entity, new exporter, dashboard framework or alerting engine.

B-09 produces a small report: actual APIs tested, account/permissions, sampled fields/units, sampling cost, failure/freshness behavior and limitations. A tiny working typed collector is optional only if it fits these boundaries and gets implementation approval. If it needs broader monitoring/cross-platform design, record feasibility and defer it. Current state: documentation candidates identified; **no runtime host PoC executed by this planning change**.

### DataFlowTelemetry PoC — documented statistics first

Start with public `catalog.execution_data_statistics` under execution READ visibility and separately approved Verbose logging. Microsoft documents rows when components send downstream data and separate rows for multiple outputs. Evaluate actual `data_stats_id`, execution/task/path identifiers, source/destination names, `rows_sent`, `created_time` and `execution_path`; retain package/iteration context without inventing a universal graph. Test per-path and branching counts against known fixture expectations. Do not sum every path as a unique source-row count or equate rows sent to committed destination rows.

Evaluate source/transformation/destination count coverage, repeated observations/deduplication, cumulative counts, derived throughput over a declared interval, logging delay and observation freshness. Zero missing rows is not zero activity. Examine `catalog.execution_component_phases` for documented phase start/end timings with Performance/Verbose; overlapping phases, asynchronous components and iterations must not be represented as exact per-row CPU time. Basic logging for ordinary B execution remains unchanged; never silently enable Verbose on existing fixtures/executions.

Use a **separately approved representative Data Flow fixture** with known source, transformation, branching and destination expectations. The existing task-free/Script Task B-02 packages cannot prove Data Flow telemetry; A-10/A-11/B-02 fixtures remain unchanged. Source/build/deploy/execute/logging scope needs separate approval before any external mutation. First record what the public catalog actually supplies, its row-level visibility, overhead and retention. Investigate Windows-native collection only if it offers an additional evidenced capability and remains small/read-only; no assumed SSDT debugger parity, arbitrary attachment, injected package instrumentation or general profiler.

B-09's report must distinguish documented feasibility from real fixture results, unsupported/unknown fields, count/timing semantics and freshness/overhead limits. An evidence-supported negative result or implementation deferral satisfies the PoC boundary; a production telemetry UI does not. Current state: documentation supports path counts and phase timings, but **representative real telemetry tests are Not Executed**. Deferred: production history, metrics pipeline, visualization/alerts and debugger-equivalent monitoring.

## 1. Contracts and authority boundaries

The following are minimum implementation targets, not a universal provider SDK. Final C# signatures/DTOs are pinned by each task's contract tests before its implementation.

| Contract | Minimum durable content and rules |
| --- | --- |
| Pipeline | Stable UUID; ProviderInstance/type; immutable provider-qualified NativeDefinitionReference; display name, optional bounded description, enabled flag, version/ETag; accepted provider-owned baseline document/fingerprint; registering/reconfirming issuer/subject and timestamps. No PipelineVersion aggregate. |
| Run | Stable UUIDv7/admission identity; Pipeline/version and ProviderInstance identity; immutable native binding/baseline and non-secret execution-context snapshot; exact requesting issuer/subject; phase/outcome; admission/ownership/fence/lease/slot; safe reason and timestamps. Mutable progress never rewrites intent. |
| RunAttempt | UUID, Run/provider identity, attempt number (initially 1); lifecycle and optional provider outcome; create-operation ID; durable per-call admission/acknowledgement markers; once-set native ID and sealed native reference; latest approved provider observation and freshness; reconciliation condition and safe diagnostics. Zero attempts are valid for preflight rejection. |
| NativeRunReference | ProviderInstance/type + explicit provider schema version + canonical native identity. SSIS document retains execution ID, exact creation identity/time and creation-context verification facts; never a bare numeric ID. |
| Provider observation | Exact native status, approved identity/revision/time facts, observed-at and freshness. SSIS-owned schema; normalized outcome is a separate verified interpretation. |
| Unattributed native observation | Actual provider-qualified execution reference, approved observed metadata/status/freshness and optional independently proven FlowPlane correlation. No synthetic Run/Attempt or ownership inferred from package/path/time/caller similarity. |
| Audit record | Significant action, actor, resource IDs, time, safe reason and non-secret evidence references. Not event sourcing or a copy of every poll/native message. |

**Domain** owns Pipeline/Run/Attempt invariants and provider-independent references, not SSIS columns. **Application** owns registration/reconfirmation, durable admission, ordering, reconciliation and authorization-context handoff. **Infrastructure** owns EF mappings, canonical-document persistence, locks, fences and short transactions; it validates documents through provider-owned codecs, not SSIS JSON inspection. **Providers.Ssis** owns targeted resolution, profile/revision projection, procedure/query implementation and status interpretation. **Api** owns JWT/operator/resource authorization, HTTP/ETag/idempotency handling and host-service composition. **Frontend** owns only the bounded authenticated journey.

Do not reuse discovery state/generation tables as Run storage or turn the discovery runner into a generic job framework. Reuse tested patterns, not lifecycle identity.

## 2. Registration, stable identity and revisions

1. Authorize the exact trusted operator and selected ProviderInstance. Apply the current endpoint policy and enabled/configuration/credential context.
2. Select a currently published eligible executable observation; bind the request to its generation and expected baseline. Close the PostgreSQL reader before native I/O.
3. Target-resolve its live definition and parent scope. Verify exact identity, entry point, compatible formats and revision/approved metadata; recheck context before atomic registration/audit.
4. Enforce one Pipeline per exact provider-qualified native identity. Index SHA-256 of canonical identity, but compare canonical identity on every collision/conflict; a digest collision is not a duplicate registration. Concurrent losers receive a safe conflict/existing-resource result.
5. Pipeline storage has no retention-dependent FK to discovery generations. Refresh cleanup cannot delete its identity/baseline.

Reuse `flowplane.ssis/native-definition-reference.v1`: exact folder/project/package names plus provider identity and schema version. FlowPlane equality is ordinal, preserving Unicode/case without normalization; SQL lookup follows actual catalog collation, then verifies returned identity. Same native path in another ProviderInstance is not the same reference.

The accepted SSIS baseline records current active `object_version_lsn`, project/package formats, package GUID/version facts and a canonical fingerprint of approved current parameter/reference metadata. Refresh this evidence even if LSN is unchanged. Active is not necessarily the historically newest version after a restore. Numeric IDs, deployment times and GUIDs are observations, not replacements for stable reference identity. Preserve native timestamp precision in canonical provider documents; do not make identity depend on PostgreSQL timestamp rounding. API native Int64 values remain decimal strings.

Same-path redeployment retains the Pipeline UUID but invalidates an old accepted baseline. Missing/renamed/invisible/incompatible targets become unavailable/changed; do not infer deletion, silently relink or execute another revision. A privileged operator may explicitly reconfirm a freshly resolved baseline using If-Match and the expected live fingerprint; audit it, increment Pipeline version and apply it only to future Runs. Reconfirmation is blocked while related execution remains unsettled. A different path/provider requires a new Pipeline.

Metadata evidence is not a transactionally frozen SSISDB snapshot. A final pre-start check and SSIS's own outdated-project start rejection reduce races; they do not prove atomic exclusion of external catalog changes.

## 3. Strict initial execution profile

Use one versioned SSIS profile: non-Scale-Out, 64-bit, asynchronous, Basic logging, no supplied project/package values and `reference_id=NULL`.

For B's first executable profile, require positive current metadata evidence of **zero project parameters and zero selected-package parameters**, plus a separately reviewed self-contained fixture with no environment dependencies. Do not merely check that required values appear set. No environment selection or value resolution is available. Parameterized packages may be registered, but execution is rejected as `ExecutionProfileIneligible`; supporting defaults/parameterized execution is deferred to its dedicated slice.

Supplying no values does **not** generally mean an SSIS package has no effective configuration: server-side defaults, parameter bindings, connection configuration or environment references can influence execution. Catalog metadata alone cannot prove arbitrary package behavior or absence of external effects. B-02 verifies the source-controlled fixture's self-contained profile; production-like arbitrary packages are not certified by that fixture.

Re-resolve current project/selected-package metadata and environment-reference/binding facts before create and before start. Compare approved metadata fingerprints as well as LSN. Reject new parameters/dependencies or changed evidence even with unchanged LSN. Never retrieve `design_default_value`, `default_value`, environment-variable values or sensitive execution values to establish eligibility. The value-free fingerprint makes no claim to detect changes to values it deliberately never reads; the strict zero-parameter fixture boundary avoids relying on them.

## 4. Lifecycle and terminal semantics

| Subject | Legal B transitions | Interpretation |
| --- | --- | --- |
| Run | Requested → Queued → Active → Terminal | Requested/Queued may fail preflight before native work. Outcome absent until Terminal; terminal outcomes retain ADR-0005 Succeeded/Failed/Cancelled/TimedOut vocabulary. No B workload timeout/cancel feature is introduced. |
| Attempt | Created → Dispatching → Submitted → Running → Terminal | Dispatching begins at durable create admission. Submitted requires durable native correlation. Submitted → Terminal is valid for fast completion. Terminal requires a settled provider result. |
| No-native closure | No Attempt, or Created/Dispatching with explicit administrative `NoNativeCreation` closure proof | A local preflight rejection is not a fictional provider Failed outcome. A call rejection closes no-native work only with affirmative evidence that creation could not occur; an arbitrary exception is insufficient. Closed work cannot be dispatched later. |
| Reconciliation | Clear / Required / Reconciling / ActionRequired | Separate from lifecycle/outcome. Uncertain create/start or unclassified native result stays visible and fenced. |
| Observation health | Fresh / Stale / Unavailable | Connectivity/visibility failure changes freshness, not the last observed native result. |

A Terminal Run never reopens. Reconciliation may continue after Run terminality if native settlement remains unknown; Run terminality alone cannot release capacity. B's ordinary success/failure path terminates only on verified facts. Run Again is a new Run after safe settlement, not a second automatic Attempt. Native externally cancelled work may yield Cancelled; B exposes no cancel operation. Transport timeouts never imply Run TimedOut or native termination.

| Exact SSIS status | B interpretation |
| --- | --- |
| 1 Created | Submitted, not proof of start/running. |
| 2 Running | Running observation. |
| 3 Canceled | Verified correlated terminal Cancelled fact. |
| 4 Failed; 6 Ended unexpectedly | Verified correlated terminal Failed fact; preserve exact code. |
| 5 Pending; 8 Stopping | Non-terminal; continue observation. |
| 7 Succeeded | Verified correlated terminal Succeeded fact. |
| 9 Completed | Preserve exact native completion; do not infer Succeeded. Require provider-verifiable result evidence; otherwise ActionRequired, unclassified outcome and retained slot. |
| Unknown value | Preserve sanitized numeric fact, no fabricated outcome; ActionRequired. |

### External and unattributed native observation

A registered Pipeline has two separate views: FlowPlane-owned Runs/Attempts, and currently visible native executions, including those started through SQL Server Agent, SSMS or another application. A native execution without a proven durable Attempt correlation is **Unattributed**, not necessarily proven external: do not infer its originating application from caller/name/time or adopt it to resolve an ambiguous create. Correlation requires the existing durable native-call/ID evidence; the ambiguity, revision and fencing rules above are unchanged.

Use the actual `execution_id` with ProviderInstance, captured source/catalog context and verified creation identity; preserve Int64 as a decimal string. Recheck current authorization, endpoint/credential context and native row visibility on every list/detail/poll. Missing/invisible/retained-away records yield `MonitoringUnavailable`/Unknown, never proof of absence, native failure or safe release of an owned uncertain slot. Read-only monitoring grants no start/configure/cancel/deploy authority, consumes no Run slot and cannot change a FlowPlane Run's outcome without its established correlation.

Keep SSIS metadata/codecs and parameterized public-view queries in Providers.Ssis. Application exposes only a narrow provider-independent observation envelope; Pipeline and Run ownership remain unchanged. Target `catalog.executions`/`catalog.operations` by the registered source/package and exact execution IDs. Preserve exact statuses and terminal facts, with status 9 still unclassified unless sufficient provider evidence exists. FlowPlane lifecycle labels such as Submitted/Running apply only to correlated Attempts, not invented external Runs.

`catalog.executions.package_name` identifies the entry package. Consider targeted `catalog.executables` evidence for packages invoked inside its execution instance; keep the parent's actual execution ID and distinguish the observed child/package association from an independent execution. Match verified source/project/package scope, not an unqualified child filename. These executable rows are tasks/containers, may appear only during execution and do not prove all future child invocations. Where source qualification, child visibility or child-specific outcome cannot be established, report Unknown; do not invent a child execution ID or infer child success from its parent's result. B-02 must verify the public evidence available for the controlled fixture before B-09 relies on it.

Keep external native history on demand in SSISDB, separate from durable FlowPlane Run history; no new PostgreSQL table, synthetic Run or full native-history import. Proposed B-09 implementation defaults are page size 50 (maximum 100), a recent terminal-history window of 24 hours (maximum 7 days), deterministic `(created_time, execution_id)` keyset ordering and bounded query/poll timeouts. Report the requested window and continuation explicitly, not complete catalog coverage. Exact-ID monitoring and active-execution preflight are not limited by that recent-history window. Do not retain external native payloads across authorization loss or backend restart; SSISDB controls their source retention. Review numeric defaults during B-09 implementation, without expanding schema or infrastructure.

### Manual-run active-execution preflight

Immediately before durable create admission, make a read-only, targeted active-execution check for the exact target and verified child associations. Include visible Created (1), Running (2), Pending (5) and Stopping (8) obligations, including old executions outside any recent-history window. An unclassified status is Unknown, not affirmative inactivity. Retrieve only bounded existence/evidence rows, with cancellation/timeouts; do not scan the entire execution catalog into memory.

- A matching visible active execution blocks dispatch with `ActiveNativeExecutionPresent`, identifies only currently authorized native references and offers read-only monitoring. No create/configure/start call occurs.
- A failed check, denied required lookup or known insufficient scope yields `MonitoringUnavailable`/Unknown and rejects dispatch, not an empty success. A successful zero-row result is only `NoVisibleActiveExecution` under row-level visibility; it never certifies absence of executions hidden by permissions. B-02 records the actual visibility scope, and the UI must not claim a global concurrency check passed.
- This is a best-effort **visible-execution** application policy, not a distributed lock, exclusive native lease or atomic SSISDB concurrency guarantee. An external caller can start after the check, and child evidence may not yet exist. Do not broaden permissions or introduce scheduling to conceal these limitations.
- Reuse the existing durable Run/preflight-failure rules: settle the local rejected intent with its safe code and affirmative proof that FlowPlane never admitted native creation; leave no Attempt or a closed Created Attempt as applicable. That proof permits release of this local reservation, not settlement/adoption of the external execution or release of another uncertain Run's slot. A later manual retry is a new Run, never an automatic redispatch of the rejected intent.

## 5. Durable creation/configuration/start protocol

Each numbered native boundary has a committed admission token (attempt, operation kind/sequence, owner/fence and captured provider/credential context) before issuing it. Each acknowledgement is persisted/reconciled before proceeding. Database locks follow ProviderInstance → managed secret → execution coordination rows → Run/Attempt; final SQL lock order is pinned in B-06 and shared by deletion, rotation, admission and recovery. No database transaction spans native I/O.

The initial profile has four mutation steps: create, set Basic logging, set asynchronous mode and start. Store their bounded markers on RunAttempt, not a speculative general operation/job table. Persist the numeric ID once; creation facts may only be completed monotonically before sealing the full native reference. Neither the ID nor a sealed identity may be rebound.

1. Commit immutable Run intent, actor, idempotency receipt and the independent global Run slot. Return HTTP 202 only after durable commit; uncertain commit is reconciled by Run ID.
2. Freshly resolve and verify definition, accepted baseline, strict profile and actual permissions; perform the visible-active-execution preflight above before create admission. Persist initial Attempt before native mutation; read-only preflight rejection may settle with no Attempt.
3. Persist Dispatching and create admission. Call public `catalog.create_execution` once with exact names, `runinscaleout=0`, `use32bitruntime=0`, `reference_id=NULL`. Do not inherit the server's default execution mode.
4. Persist returned `execution_id` and provisional scoped native reference before **any** configure/start call; transition Submitted. Query that exact ID to verify creation identity, path and `project_lsn`, then seal the reference's creation facts before further native mutations.
5. Admit/configure only typed system parameters through `catalog.set_execution_parameter_value` (`object_type=50`): `LOGGING_LEVEL=1` and `SYNCHRONIZED=0`. Persist each successful acknowledgement. No project/package values, dump settings, caller-controlled SQL or unbounded system options.
6. Recheck definition/profile, current authority and captured context. Persist start admission, then call `catalog.start_execution` once (`retry_count=0`). Persist/reconcile acknowledgement. Successful return is not proof of workload success.
7. Observe exact correlated `catalog.executions` / `catalog.operations` (`operation_type=200`) under current visibility; durably reconcile native status/freshness and eventual outcomes.

Native-call admission is the linearization boundary: a provider/credential/authorization change before admission blocks the call. A change after admission cannot revoke that already admitted in-flight call. Lease expiry prevents **new** admissions; it cannot fence an SQL call already sent or delayed after its durable admission. A replacement worker must never repeat that uncertain operation. A late create response may monotonically attach its ID using the exact create-operation token/context, but cannot renew old ownership or authorize configure/start. This is not exactly-once execution or distributed atomicity.

### B-02 permission and authentication gate

The original B-01 gate used the approved SQL-authenticated principal, not an elevated replacement. Its failed create result is retained above. The approved replacement route must separately establish actual create, configure, start and observation behavior under the intended Windows service identity on SQL Server 2025. Inspect documented project READ/EXECUTE, execution READ/MODIFY and operation/execution visibility; an environment would require additional READ, but none is selected. Do not grant sysadmin/ssis_admin as a shortcut or assume effective permission rows or interactive-administrator tests establish service-account runtime feasibility.

The preserved SQL-authenticated test actually failed at create with **27123/state 1**, without a returned native ID. Configure/start were not reached; this is not evidence that every operation fails. The operator has now explicitly approved the Windows Agent route, subject to the scope/security checkpoints above. B-02 must obtain new approval for the precise Windows service identity and real Integrated Authentication proof; do not reuse the prior SQL execution authorization, elevate roles or use internal procedures. Registration B-03–B-05 can continue independently.

B-02 has two distinct proof tracks. **A — Interactive Windows Integrated Authentication feasibility** can proceed after completed B-01 independently of Agent Foundation #28, under separate exact interactive-identity/fixture/operation approval. Record the real interactive Windows/SQL identity, permissions and each create/configure/start/observe result. It proves only that route for that identity, not least-privileged service execution. **B — Actual Windows Service identity proof** requires foundation readiness plus separately approved service-token/SQL setup and operation scope. B-02 is not complete on A alone; B and all other required acceptance remain necessary before execution-specific implementation. No native operation in either track is authorized by this planning correction.

## 6. Ambiguity, restart and safe single-slot recovery

| Failure boundary | Durable interpretation and next action |
| --- | --- |
| Before create admission | A new owner may re-run preflight under a new fence; prove no native admission before safe no-native closure/release. |
| Create admitted; no durable ID | `NativeCreationUncertain`, ActionRequired. Never automatically call create again or attach an execution by package/caller/time heuristics. |
| ID returned; database acknowledgement unavailable | No subsequent native mutation until durable correlation is proven. Late correlation is narrowly permitted; restart without durable ID remains uncertain. |
| Crash during configure or before start | Observe known ID. Uncertain configuration yields `NativeConfigurationUncertain`; do not auto-configure/start after restart. Even an unstarted Created execution remains a native obligation. |
| Start admitted; response missing | `NativeStartUncertain`; observe the known ID only. Never automatically repeat start, even if its latest observed status is Created. |
| Known Running after backend restart | Reacquire observation ownership and query the same scoped reference; do not recreate/reconfigure/restart. |
| Missing/invisible/retained-away native row, TLS/auth/network failure | `NativeObservationUnavailable` or `NativeIdentityMismatch`; preserve internal correlation and slot, no fabricated Failed or stale native-payload disclosure. |
| Terminal persistence acknowledgement lost | Re-read by durable Run/Attempt/operation IDs; do not assume rollback, reopen or redispatch. |

One nullable global execution slot (value 1, database-enforced unique) is held until **verified safe settlement**, not merely lease expiry, process restart, Run terminality or elapsed time. The discovery slot is independent. No automatic queue drains around unresolved work.

Safe operator-visible codes also include `DefinitionChanged`, `ExecutionProfileIneligible`, `ProviderContextChanged`, `NativeResultUnclassified` and `CapacityHeldForReconciliation`. Never expose raw SQL exception/native message text. A status/detail view may show ActionRequired and safe reasons; a general ambiguity-resolution UI is not in B.

Administrative resolution requires an explicitly authorized operator, short fenced database transition and immutable audit evidence:

- Establish the exact provider/catalog context and quiescence of every former call-issuing executor; prove no delayed old call can still be issued/completed unnoticed.
- Where a native ID is known, verify exact creation identity and authoritative terminal/settlement facts. Inaccessible/missing rows or status 1 are not proof that no execution can occur.
- Where no ID is durable, require affirmative proof of no native creation (for example, durable proof the create boundary was never admitted, or a verified operation-specific pre-creation rejection). An empty time-window search, timeout, killed worker or unreturned response is not enough.
- Record operator, reason, original phase/reconciliation condition, native/operation evidence references and before/after fence/slot changes without sensitive payloads.
- Release only after such proof and fenced settlement commit. If proof is unavailable, retain capacity and ActionRequired. No force-unlock endpoint or silent risk-accepted release is authorized. Any exceptional recovery outside these proofs requires separate approval and audit; it must not be described as safe duplicate prevention.

This intentionally trades availability for avoiding accidental duplicate execution. Full automated ambiguous-create resolution belongs to its later slice, not this milestone.

## 7. Provider and credential changes

- A ProviderInstance referenced by a Pipeline cannot be rebound to another endpoint/catalog identity or deleted in B. A different endpoint requires a new ProviderInstance/Pipeline. Check this **under the first provider lock before discovery retirement**, not only at final FK deletion.
- Block username, endpoint and material TLS/configuration changes during unsettled native work. Disable prevents new execution/mutation admissions, not native cancellation; authorized recovery observation may continue against the same allowed endpoint.
- Password-only rotation for the same endpoint/principal may repair observation. Before create admission it fences old intent; after native work begins it permits read-only recovery under new credentials, not automatic configure/start continuation. Never reinterpret an existing numeric ID against another endpoint.
- Capture ProviderInstance and managed-secret record versions; recheck after password resolution and at each new native-call admission. Direct managed-secret replacement must cooperate with the same provider/secret locking protocol; test this narrow A-09 change explicitly. Do not snapshot a password.
- Where managed SQL credentials participate, require owned credentials with durable versioning. Existing environment secret resolution remains supported for its A-09/read/test uses, but an externally changed environment value is not a durable credential-version fence. This does not require a SQL password for Windows Integrated Authentication.
- For the approved Windows Agent path, distinguish existing managed SQL discovery/read credentials from native execution's Windows service identity and from the agent transport identity. Bind/version the agent assignment, enrolled identity and verified SQL source/principal in immutable execution context; changing an agent, service identity or source fences obsolete authority and never reinterprets an old numeric execution ID. The preceding managed-secret rules still apply wherever a managed SQL credential participates, not as a requirement to pass a SQL password to Integrated Authentication.
- Recheck current operator authority before new native mutations. Once admitted, execution observation is FlowPlane's durable responsibility, not attached to browser JWT lifetime. Retain exact original actor for audit without storing bearer tokens.
- Historical/current native detail disclosure still requires current authorization and native visibility. Safe FlowPlane orchestration envelopes may report unavailable/action-required without exposing cached native metadata after denial.

## 8. Minimum PostgreSQL proposal and migration boundaries

The original B-01 four-table execution proposal follows; names/types/constraints remain review targets, not generated schema or permission to migrate. The approved agent route additionally needs narrowly scoped durable enrollment/identity/revocation, assignment/version and command-receipt/fencing state. B-06/B-08 must review its exact schema and the bounded local-receipt contract before implementation; do not assume these four tables alone express secure agent ownership, reuse discovery tables, or add host-metric/telemetry-history tables. Preserve PostgreSQL orchestration authority and the existing migration-review gates.

| Table | Minimum columns/constraints |
| --- | --- |
| Pipelines | UUID PK, ProviderInstance FK/type, immutable reference schema/canonical JSONB/digest, accepted-baseline schema/JSONB/fingerprint, display/enabled/version, actors/timestamps. Unique provider+native digest with canonical collision detection; composite ownership key. |
| Runs | UUIDv7 PK/idempotency identity; Pipeline/provider composite FK; immutable context schema/JSONB and captured versions; actor; phase/outcome/safe code; global slot; owner/fence/lease; progress version/timestamps. Partial unique slot=1; validate phase/outcome/ownership combinations. |
| RunAttempts | UUID PK, Run/provider composite FK, unique Run+attempt number; lifecycle/outcome; create-operation ID and bounded admission/ack markers; once-set native correlation and sealed reference schema/JSONB/digest; latest approved observation/freshness/reconciliation; safe no-native closure evidence. Unique provider+native execution identity when present. |
| ExecutionAuditRecords | UUID PK, owned Pipeline/Run/Attempt references as applicable, actor/time/action/safe reason and bounded non-secret evidence. No provider-secret payload or full native logs. |

Use restrictive deletion and same-ProviderInstance relationships; no history cascade, no discovery-generation FK. Explicit check constraints cover nonnegative versions/fences, slot domain, lease ownership, native-reference nullability and object-shaped/versioned documents. Hashes are indexing aids, never identity proof. Canonicalize through provider codecs after PostgreSQL JSONB round trips; do not hash PostgreSQL rendering. Native IDs/revisions retain full Int64 precision; native creation identity preserves its original timestamp precision in the provider document.

Migration gate 1 (B-04): Pipelines + registration audit. Gate 2 (B-06): Runs + RunAttempts and necessary audit relationships/ownership fields. Each produces reviewed EF Up/Down, snapshot and PostgreSQL upgrade/rollback SQL before any application; then isolated PostgreSQL 18 fresh/upgrade/repeat/rollback/concurrency acceptance. Guard rollback against unsettled/admitted/uncertain work under an adequate DDL lock, not a racy precheck. Do not alter A-09 encryption schema or A-11 discovery semantics; no automatic API-startup migrations.

## 9. Task graph and implementation gates

| Task | Issue | Blocked by | Independently reviewed deliverable |
| --- | --- | --- | --- |
| B-01 | [#17](https://github.com/aaliakbarian/flowplane/issues/17) | Milestone A complete | Complete/closed original checkpoint; preserved history. |
| Agent foundation | [#28](https://github.com/aaliakbarian/flowplane/issues/28) | B-01; ADR/security and implementation approval | Install/uninstall/setup, pairing/mTLS/revocation/rotation, AgentHealth and synthetic journal primitives; no SSIS adapter or execution. |
| B-02 | [#18](https://github.com/aaliakbarian/flowplane/issues/18) | B-01; separate external-operation approval | A: independent interactive Windows feasibility. B: actual service-token proof requires foundation readiness; A alone cannot complete B-02. |
| B-03 | [#19](https://github.com/aaliakbarian/flowplane/issues/19) | B-01 | Targeted definition/revision/profile contracts. |
| B-04 | [#20](https://github.com/aaliakbarian/flowplane/issues/20) | B-03 | Pipeline persistence/registration API; first migration gate. |
| B-05 | [#21](https://github.com/aaliakbarian/flowplane/issues/21) | B-04 | Minimal registration UI. |
| B-06 | [#22](https://github.com/aaliakbarian/flowplane/issues/22) | B-02, B-04 | Run/Attempt and reviewed agent-context/receipt persistence; migration gates. |
| B-07 | [#23](https://github.com/aaliakbarian/flowplane/issues/23) | B-02, B-03, B-06, agent foundation | Provider-specific SSIS Integrated Authentication adapter hosted in the foundation service; no duplicate service/enrollment work. |
| B-08 | [#24](https://github.com/aaliakbarian/flowplane/issues/24) | B-06, B-07, agent foundation | Durable Run delivery, one-shot admissions, ownership/fencing and recovery using foundation transport. |
| B-09 | [#25](https://github.com/aaliakbarian/flowplane/issues/25) | B-08 | Observation/recovery/history; two bounded PoC reports, optional small collectors only. |
| B-10 | [#26](https://github.com/aaliakbarian/flowplane/issues/26) | B-05, B-09, agent foundation | Manual Run/history plus completed operator download, pairing, revocation and basic health UI. |
| B-11 | [#27](https://github.com/aaliakbarian/flowplane/issues/27) | B-10 | Real execution/service lifecycle and independent security acceptance; both PoC reports. |

The eleven B-01–B-11 issues and separate foundation [#28](https://github.com/aaliakbarian/flowplane/issues/28) belong under #16 / Milestone #2; native blocked-by relationships mirror this table. Remove only the former #28 → #18 issue-blocking edge so B-02 track A and foundation can proceed independently after B-01. The foundation requirement for track B remains a B-02 acceptance gate, not a whole-issue blocker. B-02 still gates **execution-specific implementation**, including B-06/B-07; foundation still gates agent-specific implementation in B-07/B-08/B-10. Preserve every other native relationship and the independent registration lane. Issue #28 was allocated by GitHub after a duplicate check, not invented/reserved before creation.

### B-01 — Planning checkpoint (completed historical acceptance)

**Files:** This file; discovery links in `README.md` and `docs/development/mvp-implementation-plan.md`. **Produces:** approved design/task contracts, not executable interfaces.

- [x] Original B-01 design/decomposition completed, committed and issue #17 closed. This later scope amendment does not reopen it or alter Accepted ADRs.
- [ ] Review the separate Windows Agent scope/security checkpoints before affected implementation; this amendment authorizes no service installation or native execution.

### Separate Windows Agent foundation child

**Issue:** [#28 — Windows Agent Foundation](https://github.com/aaliakbarian/flowplane/issues/28); parent #16, Milestone #2. **Blocked by:** completed B-01 only, plus ADR/security and task-specific implementation approval. **Produces:** the installable/uninstallable service shell, protected local identity/setup IPC, operator pairing flow, dedicated HTTPS/exact-pin mTLS, rotation/revocation/incarnation, minimal non-secret journal primitives and basic AgentHealth. No SQL adapter, native execution, Run admission, generic host model or dynamic remote commands. It is separate from B-07 and can support B-02's later intended-account proof without an execution-dependency cycle.

- [ ] RED: unknown/expired/no-EKU certificates, forged headers, wrong listener, revoked identity over an open connection, exact-DER substitution, duplicate/concurrent grants and cross-assignment replay fail closed.
- [ ] RED: two grants/rotation/revocation races serialize against agent/assignment versions; pending proof and confirmation have idempotent receipts but never grant operational authority. Test lost responses/restart without retaining a pairing code.
- [ ] GREEN: generic secret-free package and local helper automate identity/pairing; no manual CSR/fingerprint handling. Verify matching phrase before atomic activation, exact issuer/subject authorization and protected IPC. Normal rotation requires settled work; old credentials become receipt-only, then retire.
- [ ] RED/GREEN: service-account/token/ACL proof, journal flush/corruption/disk-full tests with synthetic operations, truthful Unknown execution readiness, no service binary/config writes, no deletion of unresolved evidence on uninstall. Native operations are not foundation tests.
- [ ] Review agent/enrollment schema and generated migration/SQL before any application; real isolated PostgreSQL and real TLS tests follow separate approval. Local certificate provisioning, service installation and SQL grants remain independent external-operation gates.
- [ ] Deliver minimal operator download/pair/status/revoke API/UI so the foundation is independently verifiable; B-10 integrates that journey with manual Runs rather than rebuilding it. No production monitoring or automatic updater.

### B-02 — Controlled fixtures and authentication proof

**Files:** Existing uncommitted `tests/fixtures/ssis/FlowPlaneBExecutionFixture/` sources/tests are preserved; a future opt-in `RealSsisExecutionFeasibilityTests.cs` and the narrow Windows Agent security/protocol checkpoint require separate implementation approval. No changes to A-10/A-11 fixtures. **Produces:** preserved SQL failure evidence plus separately approved Windows Integrated Authentication proof and reviewed service/transport contracts, not runtime certification or a general agent framework.

- [ ] **A — Independent interactive proof:** after B-01 and exact external-operation approval, verify each approved native operation under the selected interactive Windows identity; capture actual SQL authentication/principal and permissions. This does not require foundation completion and does not prove service-account execution.
- [ ] **B — Service-token proof:** after foundation readiness and separate identity/grant/operation approval, verify the same boundaries under the actual running service token. Retain A and B as separate evidence. Interactive A alone cannot complete B-02 or unlock execution-specific implementation.

- [ ] RED/GREEN: Preserve the failed SQL-authenticated create characterization; add separately approved service-identity tests for non-Scale-Out/64-bit create, approved system settings, once-only start and exact execution/operation observation. An absent/unapproved fixture is blocked, never passed/skipped acceptance.
- [ ] Review source-controlled harmless Success, deterministic Failure and bounded LongRunning fixtures before separate build/deploy/execute authorization. Use supported Windows tools; no Linux SSIS runtime or application deployment feature.
- [x] SQL-authenticated create proof stopped at 27123/state 1; no ID returned, no configure/start retry. Preserve this failure rather than marking the old SQL proof successful.
- [ ] Review Windows service identity, least privilege, enrollment/transport/receipt/fencing contracts; under new operation approval prove create/configure/start/observation separately with Integrated Authentication. Stop on failure; no elevation or hidden transport fallback.
- [ ] Separately prove visibility of a controlled execution started outside FlowPlane through an approved external client (SQL Server Agent, SSMS or another application). Record the actual observing principal/authentication route (SQL read credential versus Windows service identity), exact native ID, visible statuses and permission scope; do not conflate their visibility. No automatic Run/Attempt is created. Do not claim all launcher types were tested if only one was exercised; hidden rows remain Unknown.
- [ ] RED/GREEN: Hidden/missing rows do not prove absence. Verify targeted `catalog.executables` child-package association and its limitations; any added harmless parent/child fixture and external start require separate approval. No A fixture or permission modification is authorized by this plan.
- [ ] GREEN: All approved native checks execute on the actual Windows identity; later end-to-end orchestration from native WSL and the non-root backend container goes through the authenticated agent, not Linux SQL-authenticated execution. Confirm profile/entry points and unchanged A fixtures. No skips counted as evidence.

### B-03 — Targeted resolution and baseline

**Files:** New `src/backend/FlowPlane.Application/Pipelines/DefinitionResolution.cs`; new `src/backend/FlowPlane.Providers.Ssis/Execution/SsisDefinitionResolver.cs` and `SsisExecutionProfileCodec.cs`; reuse existing `Discovery/SsisNativeDefinitionReferenceCodec.cs` and metadata/fingerprint primitives. Tests: new `tests/unit/FlowPlane.Providers.Ssis.Tests/SsisDefinitionResolverTests.cs` and Application contract tests in `tests/unit/FlowPlane.ProviderConfiguration.Tests/`. **Produces:** `IProviderDefinitionResolver.ResolveAsync(ProviderInstance, NativeDefinitionReference, CancellationToken) -> Task<ResolvedDefinition>`; ResolvedDefinition carries validated provider-owned baseline/fingerprint, compatibility, availability and separate execution-profile eligibility.

- [ ] RED: `Unchanged_lsn_changed_bindings_invalidates_baseline`, exact Unicode/case/provider identity, unknown format, non-entry point, no visibility, revision race and zero-parameter profile tests fail meaningfully before implementation.
- [ ] GREEN: Implement bounded targeted public-view queries, typed baseline/profile codec and current-context checks; no complete catalog rescan, artifact parsing or default/value retrieval.
- [ ] Run SSIS/provider-contract tests using `dotnet test <project.csproj> --configuration Release --nologo -m:1`; expect all new and A-10/A-11 regression assertions to pass. Independent contract review before B-04.

### B-04 — Pipeline persistence and API

**Files:** New `Domain/Pipelines/Pipeline.cs`, `Application/Pipelines/PipelineRegistrationService.cs`, `IPipelineStore.cs` under existing backend project roots; new `Infrastructure/Persistence/Pipelines/` records/store/mappings; `Api/Pipelines/PipelineApi.cs`; modify DbContext/DI and `Infrastructure/Persistence/ProviderInstanceStore.cs`. Tests: new `FlowPlane.Persistence.Tests/PipelineStoreTests.cs`, `FlowPlane.Api.Tests/PipelineApiTests.cs` and domain/application tests. **Consumes:** B-03 resolver. **Produces:** explicit registration, list/detail, versioned enable/display update and baseline-reconfirmation operations; immutable native binding. Use `/pipelines` resources, candidate reference + expected generation/baseline on registration, strong ETag/If-Match on mutation, 428/412 and safe 409 conflicts.

- [ ] RED: Atomic registration/audit, exact duplicate/race/collision, generation-independent retention, stale ETag/baseline, current visibility and protected provider deletion tests. Prove deletion rejects before discovery retirement changes.
- [ ] GREEN: Implement only registration persistence/application/API and guard boundaries. No Run/native execution. No browser-supplied secret reference or database-shaped SSIS core resource.
- [ ] Generate registration/audit migration and SQL; **stop before application**. After migration approval, run actual isolated PostgreSQL fresh/upgrade/rollback/constraints/JSONB tests and API authorization tests; do not count skips as pass.

### B-05 — Registration UI

**Files:** New focused Pipeline components/client/tests under `src/frontend/src/` beside existing provider UI; minimal App integration. **Consumes:** B-04 endpoints/ETags. **Produces:** candidate registration, registered identity/status and explicit baseline reconfirmation journey, no execution.

- [ ] RED: Component tests for registration, duplicate/stale context, reconfirmation, permission failure, logout/provider switch and no automatic registration.
- [ ] GREEN: Minimal existing-stack UI; distinguish discovery eligibility, registration and strict execution-profile eligibility. Parameterized registration does not unlock execution.
- [ ] Run existing containerized frontend typecheck/Vitest/build and mocked browser contracts, then authorized live registration acceptance separately. Preserve OIDC, Nginx modes and no host Node requirement.

### B-06 — Run/Attempt contracts and storage

**Agent refinement:** Include versioned agent/source/service-identity context and exact command/admission/receipt correlation. Consume the foundation's reviewed enrollment/identity/revocation persistence; review Run integration and restart-safe receipt retention with B-08 before generating any affected migration. Test duplicate delivery, wrong/revoked agent, agent reassignment, stale fences and lost receipts without adding a universal Host/metrics schema.

**Files:** New `Domain/Execution/Run.cs`, `RunAttempt.cs`; `Application/Execution/ExecutionContracts.cs`, `IExecutionStore.cs`; `Infrastructure/Persistence/Execution/` records/store/mappings. Modify DbContext/DI, ProviderInstanceStore and `Infrastructure/Secrets/ManagedSecretStore.cs` only for reviewed execution fencing. Tests: new `ExecutionLifecycleTests.cs`, `ExecutionStoreTests.cs`, plus existing secret/deletion regressions. **Produces:** immutable ExecutionContext, durable call-admission/correlation tokens, execution lease/fence and independent slot; no discovery-table reuse.

- [ ] RED: Legal/illegal transitions, no-native closure without false native outcome, at-most-one initial attempt, one global slot, terminal immutability, same-provider ownership and credential/deletion races.
- [ ] GREEN: Persist section 8 schema and section 5 admission invariants. Each native-call marker is compare-and-set under current owner/fence/context; close or release only with settlement proof.
- [ ] Generate the execution migration and SQL; **stop before application**. After approval, real PostgreSQL tests cover fresh/upgrade/repeat/guarded rollback, canonical JSONB/native timestamp precision and secret-lock regressions. No native execution in these storage tests.

### B-07 — SSIS execution adapter

**Mandatory agent refinement:** Host SSIS mutation code in the separately delivered foundation service under the proved account. B-07 owns the provider-specific adapter, not service packaging, pairing or transport. Allowlist typed commands and exact SQL endpoints; test least privilege, no arbitrary SQL/shell, service restart and preservation of unsettled receipts. SSIS codecs/SQL stay provider-owned; no SSIS runtime in Linux Compose. Do not implement execution until B-02 and its own contracts are approved.

**Files:** New `Application/Execution/IProviderExecution.cs`; provider-owned `Providers.Ssis/Execution/SsisExecutionAdapter.cs`, `SsisNativeRunReferenceCodec.cs`, `SsisExecutionObservationCodec.cs`; focused provider unit tests. **Consumes:** B-03 resolved evidence and B-06 durable admission tokens. **Produces:** separate `CreateAsync`, `ConfigureAsync`, `StartAsync`, `ObserveAsync` operations with typed provider results; mutation operations require their exact committed admission token. The coordinator, not the adapter, persists correlation between calls.

- [ ] RED: Fault injection at every call/response boundary, forbidden profile, changed revision, SQL/TLS/auth failure and no sensitive/raw diagnostics.
- [ ] RED: Visible active top-level/verified child execution blocks create with `ActiveNativeExecutionPresent`; old active rows are not excluded by history windows. Denied/unavailable checks fail closed, zero visible rows make no global-absence claim, and an external-start race remains explicitly non-atomic.
- [ ] GREEN: Add the targeted read-only preflight before create admission. Reuse safe no-native preflight closure and local slot release, never correlation/adoption of an unattributed execution or release of an already uncertain slot.
- [ ] GREEN: Public procedures only; exact section 5 arguments, once-only call issuance, timeout classification as uncertain where effect is unknown. No automatic retry policy around mutation calls.
- [ ] Run provider/unit/architecture regressions, then separately authorized B-02-fixture integration checks. Verify lossless IDs, exact native status and no claim of cross-database atomicity.

### B-08 — Admission and hosted ownership

**Mandatory agent refinement:** Reuse foundation pairing/identity/TLS/revocation/health; implement only Run-specific bounded command delivery and Core/agent operation protocol. PostgreSQL admission is durable before HTTP 202 or agent dispatch. Acknowledge accepted/admitted/may-have-invoked/result/Core-persisted phases separately; replay receipts, never native mutations or lost one-shot permits. Renew/fence ownership; test backend/agent crash, partitions, stale/revoked peers and lost create/start acknowledgements. Native admission and recovery atomically recheck foundation authorization versions. No broker or universal remote-command service.

**Files:** New `Application/Execution/ManualRunService.cs`, `ExecutionCoordinator.cs`; `Api/Execution/RunApi.cs`, `ExecutionHostedRunner.cs`; execution-store admission/lease implementation. **Produces:** `POST /pipelines/{id}/runs` (UUIDv7 Idempotency-Key, expected Pipeline ETag, no parameter body) and durable 202 receipt; execution of that accepted Run only. Status/history read completion follows B-09/B-10.

- [ ] RED: Same-key actor/provider/precondition misuse, simultaneous cross-Pipeline admission, UUIDv7 database-clock skew, lost admission commit acknowledgement and crashes before/after every native boundary.
- [ ] GREEN: Retain idempotency with Run history; validate new-key time within ±5 minutes of PostgreSQL time, but allow old committed receipt replay. One Run slot, no backlog or detached Task.Run. Use browser-compatible UUIDv7, not crypto.randomUUID().
- [ ] Test real PostgreSQL ownership/renewal/expiry and direct credential replacement. Expired workers cannot admit new calls; a previously admitted call may still complete. Test late-ID correlation without stale start and zero duplicate creates after restart.
- [ ] Keep single-backend-replica as initial supported topology; durable fences protect takeover but do not remotely revoke already admitted SQL. Browser disconnect does not cancel work. Review hosted scope disposal and no bearer/password retention.

### B-09 — Observation, reconciliation and history

**Scope refinement:** Mandatory agent/execution recovery and AgentHealth remain separate from optional HostObservations and DataFlowTelemetry. Deliver the two bounded feasibility reports above, including actual/not-executed evidence, permission/overhead/freshness limitations and explicit defer decisions. Tiny typed read-only collectors may be proposed, but production monitoring, persistence and visualization are not required. A representative Data Flow fixture and Verbose execution need new approval; do not mutate the existing fixtures.

**Files:** New `Application/Execution/ExecutionReconciler.cs`; provider `Execution/SsisExecutionObserver.cs`, `SsisExecutionDiagnostics.cs`; execution-store bounded history/recovery methods; focused new unit/PostgreSQL/real tests. **Consumes:** exact stored native reference/operation IDs, or a registered Pipeline's source binding for separate read-only native queries. **Produces:** verified correlated outcomes, independent freshness/reconciliation, indexed Run history/detail, bounded external/unattributed observation and safe diagnostic DTOs; no new external-history persistence.

- [ ] RED: Exact status 1–9/unknown, fast completion, restart Running, native retention/invisibility, lost commit acknowledgements and uncertain create/start. Status 9 must not pass as Succeeded.
- [ ] RED: External/unattributed executions remain observable with actual provider-qualified IDs and zero new Runs/Attempts. Same path/time/caller cannot correlate an ambiguous create. Cover distinct sources, parent/child association, missing visibility, unknown child outcomes, bounded windows/keysets and fresh authorization on every poll.
- [ ] GREEN: Read exact execution/operation rows; bounded allowlisted diagnostics (safe numeric types/times/codes and generated summaries). Do not retrieve raw `message`, DiagnosticEx/context/value payloads or copy native logs.
- [ ] GREEN: Implement the separate read-only native list/detail observation boundary using targeted public views and provider-owned metadata. Apply section 4's bounded history/visibility rules without granting control, mutating Run lifecycle or retaining external payloads in PostgreSQL.
- [ ] Prove settlement/slot-release conditions, no-native closure and administrative audit rules with real PostgreSQL races. No force unlock, general resolution UI or lease-expiry settlement.
- [ ] Under separate approval, restart the backend during LongRunning and observe the same native ID to verified terminality without redispatch. Preserve A-09–A-12 regressions and current native visibility before detail disclosure.

### B-10 — Manual Run/status/history experience

**Mandatory agent refinement:** Integrate the foundation's operator-only package download/pairing/revocation/AgentHealth journey with registration and manual Runs, without duplicating it. Pairing requires no manual CSR, authenticated HTTPS and a matching local/UI phrase. Do not embed credentials in packages or URLs. Distinguish agent health, optional agent-host samples and target SQL-host evidence; unavailable optional telemetry must not block a valid manual Run. No telemetry dashboard framework, universal host browser or fleet-management UI.

**Files:** Complete `Api/Execution/RunApi.cs` with bounded `GET /pipelines/{id}/runs` and `GET /runs/{id}`, plus the narrow authenticated read-only native-observation list/detail routes beside it; new Run/native-observation client/detail/history components/tests in `src/frontend/src/`. **Consumes:** B-08 durable receipt and B-09 safe history/observation. **Produces:** manual Run, status/reload recovery, safe history, a distinct visible-native-executions view and Run Again as a new intent after settlement.

- [ ] RED: Ambiguous admission retry reuses key, reload recovers existing Run, capacity/changed-baseline/profile errors, operator/visibility denial clears native detail and no unsupported Cancel/Deploy control.
- [ ] RED/GREEN: Distinguish FlowPlane-owned Runs from Unattributed native executions, actual native ID/status, monitoring availability and bounded history scope. Active-preflight rejection offers monitoring only; hidden/unknown activity is not displayed as no activity. Never offer Adopt/Run Again/control on an unattributed execution or infer its launcher. Logout, provider change and visibility failure clear native payloads.
- [ ] GREEN: Show Run phase/outcome, exact native status/ID, freshness and action-required codes separately. No invented progress percentage, implicit retry attempt or execution-success claim from permission evidence.
- [ ] Containerized frontend typecheck/tests/build, API contract tests and both Nginx modes pass; distinguish mocked browser evidence from live execution acceptance.

### B-11 — Integrated acceptance and closure gate

- [ ] Mandatory Windows service download/install/enroll/authenticate/revoke/uninstall and AgentHealth journey passes under the approved least-privileged identity. Wrong/revoked/expired identity, replayed enrollment/commands, forbidden targets/commands and secret leakage fail closed; review installer privileges and receipt preservation separately from SQL privileges.
- [ ] Backend and agent restarts/transport loss at every admitted call boundary preserve native correlation, exact-ID observation, uncertainty and the single slot; revocation never fabricates native cancellation. A-09 secret/root protection and A-10/A-11/A-12 behavior remain intact.
- [ ] Both PoC reports record feasibility and limitations; a justified defer/negative finding is acceptable, pretending Not Executed is passed is not. No production host monitoring or Data Flow visualization prerequisite.

**Files:** Extend real opt-in SSIS integration tests and browser acceptance contracts; add `docs/development/milestones/milestone-b-live-acceptance.md` only when evidence exists. **Consumes:** all preceding gates. **Produces:** evidence, not production/native-runtime certification.

- [ ] Separately approve exact fixture deployment/execution/test-principal scope; preserve A fixtures, existing credentials/root and SSISDB permissions unless explicitly approved otherwise.
- [ ] Live authenticated registration → durable manual Run → correlated native result succeeds; deterministic failure maps truthfully; LongRunning survives backend restart through observation of the same execution.
- [ ] Under separate external-start approval, observe a controlled native execution with no FlowPlane Attempt correlation; verify zero auto-created Runs/Attempts and active-preflight rejection before native create. After verified terminal settlement, a new manual Run may proceed. Prove current visibility, bounded history and parent/child evidence without heuristic adoption; hidden-visibility races use doubles unless separate fixture/permission approval is granted.
- [ ] Exercise lost responses safely through controlled doubles/proxies and database faults; no duplicate call after ambiguity, safe capacity retention and audit. Do not manufacture unsafe real external-call failures solely to get coverage.
- [ ] Full Release/unit/architecture tests, real isolated PostgreSQL concurrency/migrations, real Windows-service execution orchestrated by native WSL/non-root backend paths, container frontend and both browser serving paths. No skipped real tests counted as passed.
- [ ] Final independent review, security audit/redaction, documentation/links/diff checks, human commit gate and exact-HEAD Foundation CI precede any proposed issue/milestone closure. No automatic closure.

## 10. Verification ownership and remaining gates

During B-01 run only documentation/static checks. The following are later acceptance commands, not permission to execute database/native mutations now:

```sh
dotnet restore FlowPlane.slnx
dotnet build FlowPlane.slnx --configuration Release --no-restore -warnaserror
dotnet test FlowPlane.slnx --configuration Release --no-build --no-restore --nologo -m:1
dotnet package list FlowPlane.slnx --vulnerable --include-transitive --format json
dotnet format FlowPlane.slnx --verify-no-changes --no-restore
sh tests/documentation/foundation-contract.sh
git diff --check
```

Unit doubles prove state/fault ordering, not native feasibility. Real isolated PostgreSQL proves locks/constraints/commit recovery, not SSIS runtime behavior. B-02 and B-11 real native tests provide the separate execution evidence; CI remains SSIS-independent. Any missing fixture/authorization or skipped real test is reported Blocked/Not Executed, never success.

Remaining approvals: final documentation commit/CI gate; foundation implementation/schema and local HTTPS provisioning; separate B-02 A interactive identity/operation scope and B service/SQL identity/operation scope; each generated execution/agent migration before isolated and normal-development application; task-specific independent security reviews and final live acceptance. Foundation proceeds only with synthetic non-SSIS contracts until B-02's full real gate; registration remains independent. ADR acceptance alone authorizes no implementation, migration, service installation or fixture execution. Binding, mutation ordering, uncertainty/slot settlement and provider boundaries cannot silently change.

Recommended first Windows Agent implementation gate is **foundation #28's bounded contract, RED/GREEN plan and schema review**, followed by explicit implementation approval. B-02 A interactive feasibility may be separately authorized in parallel; B actual service-token proof follows foundation readiness. Preserve the failed SQL proof and do not complete B-02 on A alone. **B-03** registration remains independently available after its own approval. No implementation starts under this planning clarification.

## Microsoft behavior references

These document public behavior/permissions, not evidence that this environment can execute. B-02 records actual results per operation.

- [catalog.create_execution](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-create-execution-ssisdb-database?view=sql-server-ver17)
- [catalog.set_execution_parameter_value](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-set-execution-parameter-value-ssisdb-database?view=sql-server-ver17)
- [catalog.start_execution](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-start-execution-ssisdb-database?view=sql-server-ver17)
- [catalog.executions](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-executions-ssisdb-database?view=sql-server-ver17)
- [catalog.operations](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-operations-ssisdb-database?view=sql-server-ver17)
- [catalog.executables](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-executables?view=sql-server-ver17)
- [Database Engine errors 26000–27999, including 27123](https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors-26000-to-27999?view=sql-server-ver17)
- [Windows Service hosting/install/uninstall support](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service)
- [Public Data Flow path statistics and Verbose requirement](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-execution-data-statistics?view=sql-server-ver17)
- [Public component phase timings](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-execution-component-phases?view=sql-server-ver17)
- [Read-only Windows memory properties](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-operatingsystem)
- [Read-only local disk size/free-space properties](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-logicaldisk)
