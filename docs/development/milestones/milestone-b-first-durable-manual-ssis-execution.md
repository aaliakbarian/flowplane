# Milestone B — First Durable Manual SSIS Execution Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` after explicit implementation approval. Follow the task-specific RED/GREEN and independent review gates. This planning change authorizes no execution code, migration, fixture deployment or native execution. Do not commit, push or close B-01 during planning.

**Goal:** Explicitly register one discovered SSIS package, execute one durable manual Run, retain its native execution correlation, observe a verified result and recover observation after backend restart.

**Architecture:** Provider-independent Domain/Application own registration, immutable execution intent and orchestration. The SSIS provider owns native identity, revision/profile evidence, public procedure calls and observations. PostgreSQL is durable authority for admission, correlation, ownership and reconciliation; the existing backend hosts execution/observation without a new service or broker.

**Tech Stack:** Preserve .NET 10 (SDK floor 10.0.113), EF Core/Npgsql/PostgreSQL 18, Microsoft.Data.SqlClient 7.1.1 only in Providers.Ssis, React 19/TypeScript 6/Vite 8, Keycloak/OIDC and Nginx. Native WSL development and containerized frontend tooling remain unchanged; no dependency upgrade is proposed here.

**Spec:** [MVP implementation plan, Slices 2–4](../mvp-implementation-plan.md#10-slice-2--pipeline-registration), [domain model](../../domain/mvp-domain-model.md), [SSIS technical design](../../architecture/providers/ssis-provider-technical-design.md), and Accepted [ADR-0001](../../adr/0001-control-plane-execution-plane-separation.md), [ADR-0004](../../adr/0004-capability-based-data-pipeline-provider-architecture.md), [ADR-0005](../../adr/0005-execution-lifecycle-authority-and-reconciliation.md), [ADR-0006](../../adr/0006-built-in-secret-storage-and-external-root-key.md). This plan records the approved B-01 decisions and targeted refinements; it does not modify or supersede those ADRs.

## Status and authority

- Planning baseline verified on 2026-10-28: clean `main` at `c36f92d6601682719f8463d4610263e4f6a5e403`; [Foundation run 40673397391](https://github.com/aaliakbarian/flowplane/actions/runs/40673397391) succeeded for that exact commit.
- Milestone A, parent #1 and all its children remain complete/closed. Preserve their evidence and issue history.
- [Milestone B #2](https://github.com/aaliakbarian/flowplane/milestone/2), [parent #16](https://github.com/aaliakbarian/flowplane/issues/16). B-01 is **Review pending**, not Complete; B-02–B-11 are **Planned**. GitHub is the live tracker.
- Approval covers architecture and planning. B-02 fixture setup and native mutations need separate approval; no execution feasibility has been established by this planning work.

## Global constraints

- Discovery never registers Pipelines. Do not alter A-11a complete scanning, A-11b atomic publication, A-12 durable discovery capacity or safe native-checked browsing.
- Native observation is not execution ownership: visible external/unattributed executions never automatically create Runs/Attempts, acquire FlowPlane capacity or become an ambiguous create's correlation.
- Pipeline native binding is immutable. A changed path/provider requires a new Pipeline, not automatic relinking.
- One global active Run, independent of discovery capacity; no unbounded backlog. At most one initial RunAttempt and no automatic retries in B. No exactly-once claim.
- Persist each native-call admission before create/configure/start. Persist the native execution ID before any subsequent native mutation. No PostgreSQL transaction spans SQL Server I/O.
- SQL authentication is an explicit B-02 feasibility gate, not inferred from A-10 permission evidence. No silent authentication/transport fallback or elevated role.
- SQL major 17 remains Supported only for the evidenced A-10 validation boundary; 15/16 remain Unverified. B-02/B-11 add narrowly evidenced execution behavior, not blanket runtime/build certification or broader format support.
- Initial execution supplies no project/package parameter values and selects no environment reference. Only the strict self-contained profile below is executable.
- No plaintext credentials, SecretReference locators, connection strings, tokens, private keys, parameter defaults or environment values in execution snapshots, diagnostics, logs or browser artifacts.
- Preserve X.509-protected PostgreSQL Data Protection keys, encrypted managed secrets, backend-only read-only root mount, exact operator authorization, endpoint allowlisting and TLS policy.
- No cancellation endpoint, scheduling, package authoring/deployment, artifact parsing, graphs, historical SSIS version management or new infrastructure. Do not expand every deferred Milestone A capability into B.
- Development acceptance data remain disposable. Production recovery requirements remain separate; never erase/regenerate existing root material. Previously declined standalone cryptographic/log probes remain Not Executed and require fresh authorization.

## Review focus

1. SQL authentication permits metadata but not an execution operation: B-02 proves each operation independently and stops on an authentication blocker.
2. Parameter bindings or relevant metadata change with an unchanged project LSN: B-03/B-07 reject obsolete execution evidence without reading values.
3. A stale worker returns from a previously admitted native call: B-08 permits only monotonic late correlation, never renewed authority or duplicate dispatch.
4. Provider deletion retires discovery before a late FK rejects it: B-04/B-06 reject under the first provider lock, before any retirement side effect.
5. Native status 9, invisible execution or lost acknowledgement looks like completion: B-09 preserves uncertainty and capacity rather than inventing success or settlement.

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

Use the currently approved SQL-authenticated test principal, not an elevated replacement. Separately establish actual behavior of create, configure, start and observation on SQL Server 2025. Inspect documented project READ/EXECUTE, execution READ/MODIFY and operation/execution visibility; an environment would require additional READ, but none is selected. Do not grant sysadmin/ssis_admin as a shortcut or assume effective permission rows establish runtime feasibility.

Microsoft error **27123** identifies a possible SQL-authentication restriction. It is not evidence that every public execution call necessarily fails. Capture the safe code and precise failing operation; an actual authentication blocker stops execution-specific implementation for an explicit architecture decision. Do not silently introduce integrated authentication, Kerberos, Windows agents, internal procedures or alternative transports. Registration B-03–B-05 can continue independently.

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
- The initial durable execution product path requires managed owned credentials with durable versioning. Existing environment secret resolution remains supported for its A-09/read/test uses, but an externally changed environment value is not a durable execution credential-version fence.
- Recheck current operator authority before new native mutations. Once admitted, execution observation is FlowPlane's durable responsibility, not attached to browser JWT lifetime. Retain exact original actor for audit without storing bearer tokens.
- Historical/current native detail disclosure still requires current authorization and native visibility. Safe FlowPlane orchestration envelopes may report unavailable/action-required without exposing cached native metadata after denial.

## 8. Minimum PostgreSQL proposal and migration boundaries

Four new tables are proposed; names/types/constraints below are review targets, not generated schema or permission to migrate.

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
| B-01 | [#17](https://github.com/aaliakbarian/flowplane/issues/17) | Milestone A complete | This checkpoint; leave open pending review. |
| B-02 | [#18](https://github.com/aaliakbarian/flowplane/issues/18) | B-01; separate external-operation approval | Separate reproducible fixtures and per-operation SQL-auth execution proof. |
| B-03 | [#19](https://github.com/aaliakbarian/flowplane/issues/19) | B-01 | Targeted definition/revision/profile contracts. |
| B-04 | [#20](https://github.com/aaliakbarian/flowplane/issues/20) | B-03 | Pipeline persistence/registration API; first migration gate. |
| B-05 | [#21](https://github.com/aaliakbarian/flowplane/issues/21) | B-04 | Minimal registration UI. |
| B-06 | [#22](https://github.com/aaliakbarian/flowplane/issues/22) | B-02, B-04 | Run/Attempt persistence/contracts; second migration gate. |
| B-07 | [#23](https://github.com/aaliakbarian/flowplane/issues/23) | B-02, B-03, B-06 | SSIS create/configure/start adapter. |
| B-08 | [#24](https://github.com/aaliakbarian/flowplane/issues/24) | B-06, B-07 | Durable admission/hosted ownership. |
| B-09 | [#25](https://github.com/aaliakbarian/flowplane/issues/25) | B-08 | Observation, reconciliation and bounded history. |
| B-10 | [#26](https://github.com/aaliakbarian/flowplane/issues/26) | B-05, B-09 | Manual Run/status/history API/UI completion. |
| B-11 | [#27](https://github.com/aaliakbarian/flowplane/issues/27) | B-10 | Real integrated acceptance and closure review. |

All eleven are native subissues of #16; native blocked-by relationships mirror this table. B-02 gates **all execution-specific implementation**, including B-06. Registration B-03–B-05 proceeds independently after B-01 contract approval. No issue number was reserved/invented before creation.

### B-01 — Planning checkpoint

**Files:** This file; discovery links in `README.md` and `docs/development/mvp-implementation-plan.md`. **Produces:** approved design/task contracts, not executable interfaces.

- [ ] Verify clean baseline, Milestone A closure and exact-HEAD Foundation evidence.
- [ ] Review this plan and GitHub decomposition against the four targeted refinements; confirm B-02's mutation scope still needs approval.
- [ ] Run `sh tests/documentation/foundation-contract.sh` and `git diff --check`; expect pass, with no unrelated file changes. Review documentation/relative links. Do not alter test assertions to accommodate planning.
- [ ] Stop for planning review; B-01 remains open. Commit/CI and issue completion require later explicit authorization.

### B-02 — Controlled fixtures and authentication proof

**Files:** New `tests/fixtures/ssis/FlowPlaneBExecutionFixture/` sources (`.dtproj`, `.dtsx`, `Project.params` if needed by tooling, README and explicit setup/verify SQL); new `tests/integration/FlowPlane.Ssis.Integration.Tests/RealSsisExecutionFeasibilityTests.cs`. No changes to A-10/A-11 fixture sources. **Consumes:** section 3/5 profile. **Produces:** per-operation feasibility evidence and fixture baseline, not a new transport.

- [ ] RED: Add opt-in tests named `Sql_principal_can_create_non_scaleout_64bit_execution`, `Can_set_approved_system_parameters`, `Can_start_once`, `Can_observe_execution_and_operation`; absent/unapproved fixture is blocked, never passed/skipped acceptance.
- [ ] Review source-controlled harmless Success, deterministic Failure and bounded LongRunning fixtures before separate build/deploy/execute authorization. Use supported Windows tools; no Linux SSIS runtime or application deployment feature.
- [ ] Under that separate approval, run the four checks and record exact SQL build, safe error codes, public permissions and native facts. Failure due to SQL authentication stops execution work for a decision; no fallback.
- [ ] Separately prove the approved SQL principal can observe a controlled execution started outside FlowPlane through an approved external client (Agent, SSMS or another application); record its actual ID, visible status transitions and permission scope without creating any FlowPlane Run/Attempt. Do not claim all three launch mechanisms were tested if only one was exercised.
- [ ] RED/GREEN: Hidden/missing rows do not prove absence. Verify targeted `catalog.executables` child-package association and its limitations; any added harmless parent/child fixture and external start require separate approval. No A fixture or permission modification is authorized by this plan.
- [ ] GREEN: All approved real checks execute successfully in native WSL and patched non-root SDK-container paths; confirm profile/entry points and no changes to A fixtures. Record safe evidence, not secret values or raw logs.

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

**Files:** New `Domain/Execution/Run.cs`, `RunAttempt.cs`; `Application/Execution/ExecutionContracts.cs`, `IExecutionStore.cs`; `Infrastructure/Persistence/Execution/` records/store/mappings. Modify DbContext/DI, ProviderInstanceStore and `Infrastructure/Secrets/ManagedSecretStore.cs` only for reviewed execution fencing. Tests: new `ExecutionLifecycleTests.cs`, `ExecutionStoreTests.cs`, plus existing secret/deletion regressions. **Produces:** immutable ExecutionContext, durable call-admission/correlation tokens, execution lease/fence and independent slot; no discovery-table reuse.

- [ ] RED: Legal/illegal transitions, no-native closure without false native outcome, at-most-one initial attempt, one global slot, terminal immutability, same-provider ownership and credential/deletion races.
- [ ] GREEN: Persist section 8 schema and section 5 admission invariants. Each native-call marker is compare-and-set under current owner/fence/context; close or release only with settlement proof.
- [ ] Generate the execution migration and SQL; **stop before application**. After approval, real PostgreSQL tests cover fresh/upgrade/repeat/guarded rollback, canonical JSONB/native timestamp precision and secret-lock regressions. No native execution in these storage tests.

### B-07 — SSIS execution adapter

**Files:** New `Application/Execution/IProviderExecution.cs`; provider-owned `Providers.Ssis/Execution/SsisExecutionAdapter.cs`, `SsisNativeRunReferenceCodec.cs`, `SsisExecutionObservationCodec.cs`; focused provider unit tests. **Consumes:** B-03 resolved evidence and B-06 durable admission tokens. **Produces:** separate `CreateAsync`, `ConfigureAsync`, `StartAsync`, `ObserveAsync` operations with typed provider results; mutation operations require their exact committed admission token. The coordinator, not the adapter, persists correlation between calls.

- [ ] RED: Fault injection at every call/response boundary, forbidden profile, changed revision, SQL/TLS/auth failure and no sensitive/raw diagnostics.
- [ ] RED: Visible active top-level/verified child execution blocks create with `ActiveNativeExecutionPresent`; old active rows are not excluded by history windows. Denied/unavailable checks fail closed, zero visible rows make no global-absence claim, and an external-start race remains explicitly non-atomic.
- [ ] GREEN: Add the targeted read-only preflight before create admission. Reuse safe no-native preflight closure and local slot release, never correlation/adoption of an unattributed execution or release of an already uncertain slot.
- [ ] GREEN: Public procedures only; exact section 5 arguments, once-only call issuance, timeout classification as uncertain where effect is unknown. No automatic retry policy around mutation calls.
- [ ] Run provider/unit/architecture regressions, then separately authorized B-02-fixture integration checks. Verify lossless IDs, exact native status and no claim of cross-database atomicity.

### B-08 — Admission and hosted ownership

**Files:** New `Application/Execution/ManualRunService.cs`, `ExecutionCoordinator.cs`; `Api/Execution/RunApi.cs`, `ExecutionHostedRunner.cs`; execution-store admission/lease implementation. **Produces:** `POST /pipelines/{id}/runs` (UUIDv7 Idempotency-Key, expected Pipeline ETag, no parameter body) and durable 202 receipt; execution of that accepted Run only. Status/history read completion follows B-09/B-10.

- [ ] RED: Same-key actor/provider/precondition misuse, simultaneous cross-Pipeline admission, UUIDv7 database-clock skew, lost admission commit acknowledgement and crashes before/after every native boundary.
- [ ] GREEN: Retain idempotency with Run history; validate new-key time within ±5 minutes of PostgreSQL time, but allow old committed receipt replay. One Run slot, no backlog or detached Task.Run. Use browser-compatible UUIDv7, not crypto.randomUUID().
- [ ] Test real PostgreSQL ownership/renewal/expiry and direct credential replacement. Expired workers cannot admit new calls; a previously admitted call may still complete. Test late-ID correlation without stale start and zero duplicate creates after restart.
- [ ] Keep single-backend-replica as initial supported topology; durable fences protect takeover but do not remotely revoke already admitted SQL. Browser disconnect does not cancel work. Review hosted scope disposal and no bearer/password retention.

### B-09 — Observation, reconciliation and history

**Files:** New `Application/Execution/ExecutionReconciler.cs`; provider `Execution/SsisExecutionObserver.cs`, `SsisExecutionDiagnostics.cs`; execution-store bounded history/recovery methods; focused new unit/PostgreSQL/real tests. **Consumes:** exact stored native reference/operation IDs, or a registered Pipeline's source binding for separate read-only native queries. **Produces:** verified correlated outcomes, independent freshness/reconciliation, indexed Run history/detail, bounded external/unattributed observation and safe diagnostic DTOs; no new external-history persistence.

- [ ] RED: Exact status 1–9/unknown, fast completion, restart Running, native retention/invisibility, lost commit acknowledgements and uncertain create/start. Status 9 must not pass as Succeeded.
- [ ] RED: External/unattributed executions remain observable with actual provider-qualified IDs and zero new Runs/Attempts. Same path/time/caller cannot correlate an ambiguous create. Cover distinct sources, parent/child association, missing visibility, unknown child outcomes, bounded windows/keysets and fresh authorization on every poll.
- [ ] GREEN: Read exact execution/operation rows; bounded allowlisted diagnostics (safe numeric types/times/codes and generated summaries). Do not retrieve raw `message`, DiagnosticEx/context/value payloads or copy native logs.
- [ ] GREEN: Implement the separate read-only native list/detail observation boundary using targeted public views and provider-owned metadata. Apply section 4's bounded history/visibility rules without granting control, mutating Run lifecycle or retaining external payloads in PostgreSQL.
- [ ] Prove settlement/slot-release conditions, no-native closure and administrative audit rules with real PostgreSQL races. No force unlock, general resolution UI or lease-expiry settlement.
- [ ] Under separate approval, restart the backend during LongRunning and observe the same native ID to verified terminality without redispatch. Preserve A-09–A-12 regressions and current native visibility before detail disclosure.

### B-10 — Manual Run/status/history experience

**Files:** Complete `Api/Execution/RunApi.cs` with bounded `GET /pipelines/{id}/runs` and `GET /runs/{id}`, plus the narrow authenticated read-only native-observation list/detail routes beside it; new Run/native-observation client/detail/history components/tests in `src/frontend/src/`. **Consumes:** B-08 durable receipt and B-09 safe history/observation. **Produces:** manual Run, status/reload recovery, safe history, a distinct visible-native-executions view and Run Again as a new intent after settlement.

- [ ] RED: Ambiguous admission retry reuses key, reload recovers existing Run, capacity/changed-baseline/profile errors, operator/visibility denial clears native detail and no unsupported Cancel/Deploy control.
- [ ] RED/GREEN: Distinguish FlowPlane-owned Runs from Unattributed native executions, actual native ID/status, monitoring availability and bounded history scope. Active-preflight rejection offers monitoring only; hidden/unknown activity is not displayed as no activity. Never offer Adopt/Run Again/control on an unattributed execution or infer its launcher. Logout, provider change and visibility failure clear native payloads.
- [ ] GREEN: Show Run phase/outcome, exact native status/ID, freshness and action-required codes separately. No invented progress percentage, implicit retry attempt or execution-success claim from permission evidence.
- [ ] Containerized frontend typecheck/tests/build, API contract tests and both Nginx modes pass; distinguish mocked browser evidence from live execution acceptance.

### B-11 — Integrated acceptance and closure gate

**Files:** Extend real opt-in SSIS integration tests and browser acceptance contracts; add `docs/development/milestones/milestone-b-live-acceptance.md` only when evidence exists. **Consumes:** all preceding gates. **Produces:** evidence, not production/native-runtime certification.

- [ ] Separately approve exact fixture deployment/execution/test-principal scope; preserve A fixtures, existing credentials/root and SSISDB permissions unless explicitly approved otherwise.
- [ ] Live authenticated registration → durable manual Run → correlated native result succeeds; deterministic failure maps truthfully; LongRunning survives backend restart through observation of the same execution.
- [ ] Under separate external-start approval, observe a controlled native execution with no FlowPlane Attempt correlation; verify zero auto-created Runs/Attempts and active-preflight rejection before native create. After verified terminal settlement, a new manual Run may proceed. Prove current visibility, bounded history and parent/child evidence without heuristic adoption; hidden-visibility races use doubles unless separate fixture/permission approval is granted.
- [ ] Exercise lost responses safely through controlled doubles/proxies and database faults; no duplicate call after ambiguity, safe capacity retention and audit. Do not manufacture unsafe real external-call failures solely to get coverage.
- [ ] Full Release/unit/architecture tests, real isolated PostgreSQL concurrency/migrations, native WSL/non-root SDK-container execution evidence, container frontend and both browser serving paths. No skipped real tests counted as passed.
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

Remaining approvals: B-01 planning acceptance; B-02 exact fixture/provisioning/native-operation scope; an authentication architecture decision if proof fails; both generated migrations before isolated application and again before normal-development application; task-specific implementation/independent reviews and final live acceptance. Concrete DTO/column/default tuning may be refined within these contracts, but binding semantics, mutation ordering, security, slot settlement and provider boundaries cannot silently change.

Recommended next verification task is **B-02**: prepare and review its controlled fixture/proof plan, then seek external-operation approval. **B-03** is the independent implementation path if native execution approval or environment readiness is pending. Neither starts under this planning authorization.

## Microsoft behavior references

These document public behavior/permissions, not evidence that this environment can execute. B-02 records actual results per operation.

- [catalog.create_execution](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-create-execution-ssisdb-database?view=sql-server-ver17)
- [catalog.set_execution_parameter_value](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-set-execution-parameter-value-ssisdb-database?view=sql-server-ver17)
- [catalog.start_execution](https://learn.microsoft.com/en-us/sql/integration-services/system-stored-procedures/catalog-start-execution-ssisdb-database?view=sql-server-ver17)
- [catalog.executions](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-executions-ssisdb-database?view=sql-server-ver17)
- [catalog.operations](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-operations-ssisdb-database?view=sql-server-ver17)
- [catalog.executables](https://learn.microsoft.com/en-us/sql/integration-services/system-views/catalog-executables?view=sql-server-ver17)
- [Database Engine errors 26000–27999, including 27123](https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors-26000-to-27999?view=sql-server-ver17)
