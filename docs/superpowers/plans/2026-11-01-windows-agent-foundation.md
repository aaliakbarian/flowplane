# Windows Agent Foundation #28 Implementation Plan

> **For agentic workers:** After explicit implementation approval, use `superpowers:executing-plans` or the separately selected execution workflow task-by-task. Follow each RED/GREEN and independent review gate. This document authorizes no code, migration generation/application, certificate provisioning, service installation/start, SQL permission change or native execution. Do not commit, push or close issues without separate approval.

**Goal:** Deliver the smallest installable, securely paired Windows Agent with authenticated health and recoverable local evidence, without implementing SSIS execution or a general command runner.

**Architecture:** Preserve [Accepted ADR-0007](../../adr/0007-windows-agent-security-transport-and-durable-operations.md) exactly: outbound bounded HTTPS polling to a dedicated listener in the existing backend, exact client-certificate DER pins, PostgreSQL authority and a minimal local journal. Application owns provider-independent lifecycle policy; Infrastructure owns persistence; Api owns JWT/certificate/HTTP boundaries; the Windows executable owns CNG, service-token and local ACL enforcement. The separate B-07 adapter will own SSIS SQL, and B-08 will own real Run dispatch.

**Tech Stack:** .NET 10; SDK 10.0.113 with existing latestFeature roll-forward; current EF/Npgsql/PostgreSQL 18 and existing xUnit/VSTest infrastructure. Use framework System.Text.Json and cryptography. Later Windows hosting and Api certificate-auth packages must match the repository's 10.0.13 servicing baseline and pass audit; do not install or upgrade anything during planning. Preserve native WSL and containerized frontend tooling.

**Spec:** ADR-0007, [Milestone B plan](../../development/milestones/milestone-b-first-durable-manual-ssis-execution.md), [ADR-0005](../../adr/0005-execution-lifecycle-authority-and-reconciliation.md), [ADR-0006](../../adr/0006-built-in-secret-storage-and-external-root-key.md), [foundation #28](https://github.com/aaliakbarian/flowplane/issues/28) and [B-02 #18](https://github.com/aaliakbarian/flowplane/issues/18).

## Verified baseline and authority

- `main` at `a87826dec9ce7e9989818d34bff5dba41d51b386`; ADR-0007 is Accepted, committed and identical to HEAD.
- [Foundation 41303776178](https://github.com/aaliakbarian/flowplane/actions/runs/41303776178) completed successfully for that exact HEAD.
- Milestone #2, parent #16, #18 and #28 are open; B-01 #17 is closed. Some issue prose still describes the earlier uncommitted review checkpoint; committed ADR/HEAD and current issue states are the baseline. No GitHub update is made by this plan.
- Existing 18 uncommitted B-02 files are preserved: combined path/content SHA-256 `33bff3e794957b28d93936f51782e42f2b4cfa8dfc7dafb034a5cde30b6804ab`. No other pre-existing changes are to be discarded or staged.

## Global constraints

- Architecture is selected, not reopened: no persistent channel, certificate-header identity, client CA service, HTTP fallback, shared fleet credential, generic plugin/job runner or monitoring platform.
- Separate human issuer/subject, agent certificate identity, Windows service token and SQL permissions. SQL discovery credentials remain in Core; the agent never receives them. Domain/Application contain no Windows/SSIS-specific metadata or SqlClient dependency.
- No native execution handler, `/operations` dispatch endpoint, Run/Attempt tables or execution slot is implemented in #28. Polling returns typed NoWork. Journal tests use synthetic operation evidence, not native calls.
- Actual B-02 B proof can use the minimal service checkpoint and a separately approved isolated harness; it must not require B-07/B-08. Interactive B-02 A remains independent. Neither permission rows nor interactive success completes B-02 or declares service execution supported.
- Normal startup with agent control disabled preserves A-09 through A-12 behavior. Enabled agent control with invalid TLS/pinning/configuration fails closed. Never use the Data Protection PFX for TLS or agent keys.
- No automatic database migrations or grants. Schema proposal, generated migration review, isolated application and normal-development application are separate gates.
- No valuable-development-data backup prerequisite is added. Existing PFX/key ring/volumes are preserved; earlier declined probes remain unexecuted. Production recovery/signing operations remain separately reviewed.
- Windows and external SQL tests not run on WSL are Not Executed/Blocked, not successful skips. Ordinary Linux CI remains SQL/Windows-service independent.

## Review focus

1. Two grants, revocation or rotation race the same assignment: serialize agent/provider versions and prove exactly one activation (F3/F4).
2. An old TLS connection or handshake-pin snapshot survives revocation: uncached database authorization still denies every operation (F4/F5).
3. Backend commit succeeds but an HTTP acknowledgement is lost: recover immutable receipt by durable IDs, never repeat enrollment or native work (F3/F5).
4. An agent crashes with full/corrupt local storage: preserve uncertainty and identity; never clear unresolved evidence or silently create a new installation (F2/F5).
5. Installer/uninstaller or the B-02 harness runs as the wrong identity: inspect actual token/SID/ACLs, not only SCM settings, and never confuse a differently named service with the candidate account (F2/B-02 gate/F6).

## 1. Implementation phases and stop gates

| Phase | Bounded deliverable | Exit and next gate |
| --- | --- | --- |
| F1 | Portable versioned wire contracts and pure Application lifecycle policy; no I/O or service scaffold. | Focused RED/GREEN and architecture review. Recommended first coding task. |
| F2 | Windows Worker shell, protected CNG identity, restricted setup IPC, local journal and install/uninstall helper. | Build/package and synthetic fault tests. Stop before installing/starting any service. Minimal checkpoint for separately approved B-02 B harness. |
| F3 | Five-table agent-control model and transactional stores for grants, pins, assignments, current session and audit. | Generate one proposed agent migration only after schema approval; present Up/Down/snapshot/SQL, then stop before application. |
| F4 | Dedicated TLS listener, exact-pin authentication, pairing/proof/confirmation APIs and minimal pairing UI. | After isolated migration approval, real PostgreSQL/TLS races and synthetic browser tests. Stop before modifying development HTTPS trust/configuration. |
| F5 | Outbound polling, current lease/heartbeat/readiness, rotation/revocation and restart reconciliation. | Synthetic NoWork only; real TLS/DB restart tests; no native dispatch or Run storage. |
| F6 | Reproducible versioned downloadable package and complete operator download/setup/status/revoke UX. | Independent security review, separate real Windows lifecycle approval and both Nginx-mode synthetic browser acceptance. No SSIS operations. |

F2 and the backend F3 lane follow F1 independently. F4 consumes F2/F3; F5 consumes F4; F6 packages/integrates verified F2/F4/F5. Do not implement all phases in one unreviewed change. Each exit requires an explicit human review/commit gate; no automatic commit is authorized by this plan.

### Dependency preservation and B-02 seam

Keep the existing issue graph unchanged: #17 precedes #18 and #28 independently; #28 still blocks #23/#24/#26; #18 still gates execution-specific work. No new issue is needed. Track F2's minimal checkpoint inside #28 and B-02 B's acceptance checklist, not a whole-issue #28 → #18 blocker.

After separate B-02 harness implementation and external-operation approval, a fixed test-only Windows Service host can exercise the existing reviewed B fixture templates under the actual `FlowPlaneAgent` SCM name/account. It reuses F2 identity/token/journal primitives, not B-07/B-08. Use a dedicated test artifact, never ship its SQL capability or selection mode in the UI package. It accepts only an explicitly reviewed local phase/profile for the exact fixture/source, not arbitrary SQL, executable paths or remote work.

The probe must use the **same intended service name and virtual-account SID**, not `FlowPlaneAgentProbe` with a different SID. Installation requires an approved isolated window with no existing conflicting FlowPlaneAgent service; never replace/reconfigure an installed agent automatically. It may use fixed test-only SqlClient/templates under separate B-02 approval, without adding SQL dependencies to the production foundation. Record actual token/SQL identities, per-call admissions/acknowledgements and exact native ID before subsequent phases. Ambiguity stops the proof. This proves service-token feasibility only, not production Core dispatch. A remains the independent interactive proof; B and all other required acceptance are still needed to complete #18.

## 2. Project structure and responsibility

| Proposed path | Responsibility/dependencies |
| --- | --- |
| `src/agents/FlowPlane.Agent.Protocol/` | `net10.0`, closed wire DTOs/strict codec; framework-only. Shared by Api and agent, not a Domain abstraction. |
| `src/agents/FlowPlane.Agent.Runtime/` | `net10.0`, polling/session state machine and journal implementation; references Protocol only. Portable fault tests; no SQL or OS service code. |
| `src/agents/FlowPlane.Agent.Windows/` | `net10.0-windows`, .NET Worker executable, WindowsServices lifetime, CNG and token/ACL enforcement; references Runtime/Protocol. No SSIS reference in foundation. |
| `src/agents/FlowPlane.Agent.Setup/` | `net10.0-windows`, small interactive console setup/install/uninstall/pair helper using restricted local IPC. No private-key export or remote command interpreter. |
| `src/backend/FlowPlane.Application/Agents/` | Narrow lifecycle records/policies/store interfaces; no transport DTO, Windows SID, SSIS or cryptographic-library coupling. Domain remains unchanged in #28. |
| `src/backend/FlowPlane.Infrastructure/Persistence/Agents/` | EF records, mappings and transactional stores; no SSIS metadata interpretation. |
| `src/backend/FlowPlane.Api/Agents/` | Dedicated-listener boundary, certificate scheme, request mapping, operator routes and bounded download catalogue. |
| `src/frontend/src/agents/` | Reuse OIDC/client conventions for download, pairing confirmation, health and revocation; no router/framework addition. |
| `tests/unit/FlowPlane.Agent.Tests/` | Portable protocol/lifecycle/polling/journal tests with existing pinned xUnit/VSTest packages. |
| `tests/windows/FlowPlane.Agent.Windows.Tests/` | Explicit Windows identity/CNG/IPC/SCM tests under separate runtime gate, not silently skipped ordinary acceptance. |
| `tests/windows/FlowPlane.Agent.ServiceIdentityProbe/` | Separately approved B-02-only test service host. Not part of foundation download or ordinary CI execution. |

Four small agent projects separate shared serialization, portable fault-testable runtime, Windows service and local helper; they are not four services. Api references Protocol, never Runtime/Windows/Setup. Application still references Domain only. Infrastructure retains inward dependencies. Future B-07 hosts Providers.Ssis in the Windows service; only that provider production project owns SqlClient and SSIS SQL. Extend architecture coverage across `src/agents`, not just today's `src/backend` enumeration.

Keep portable projects/tests in `FlowPlane.slnx`. Introduce a separate Windows build/test solution at F2 so Linux `dotnet test FlowPlane.slnx` does not execute Windows services or label OS skips as acceptance. Explicit win-x64 cross-publish is a build result, not Windows runtime proof. Windows hosting APIs are CLI/SCM based; no WPF/WinForms/desktop runtime is needed.

## 3. Typed contracts and API surface

### Common rules

Protocol version `1`, per-message fixed schema identifier, strict JSON unmapped-member rejection, max depth 8, body limit 64 KiB and public DER limit 16 KiB. UUIDs use canonical D format; versions/fences/sequences are lossless decimal-string Int64s with positive checked bounds. No arbitrary `object`, generic command payload, free-form capability/plugin name or SQL text. Failure bodies contain allowlisted code/request ID, not exception messages.

Use classes with explicitly redacted `ToString`/debug representations for code-bearing requests/responses, not auto-printing records. Do not log/destructure HTTP bodies, Authorization headers, pairing input or private keys. The one deliberately secret-bearing response is the approved one-time pairing-code display over authenticated HTTPS with `Cache-Control: no-store`; SQL credentials/private keys never appear in any API. Code fields are transient, never in browser storage or local journal.

| Contract | Exact logical fields |
| --- | --- |
| `PairingClaimRequest` | schema, claimId, grantId, transient pairingCode, publicCertificateDerBase64; no chosen AgentId/authority. |
| `PairingClaimReceipt` | grantId, claimId, assigned agentId, pin digest, immutable server nonce, expiresAtUtc, stage, receiptVersion. No code echo. |
| `PairingProofRequest` | schema, claimId, proofRequestId, serverNonce. Actual TLS certificate supplies identity/possession. |
| `PairingStatusReceipt` | grantId/claimId, stage, expiry, verification phrase, receiptVersion. Pending access restricted to its exact pin. |
| `AgentConnectRequest` | schema, bootId/connectRequestId, agent/package/protocol version, expected current fence and bounded journal-recovery digest; no token, password or arbitrary metadata. Persist boot/request IDs locally before sending. |
| `AgentSessionStatusReceipt` | current fence, database server time, current lease expiry and safe recovery mode; no new session/admission authority. Used before reconnecting after process restart. |
| `AgentSessionReceipt` | agentId, sessionId, bootId, fence, authorizationVersion, serverTimeUtc, leaseExpiresAtUtc, recoveryMode. |
| `AgentPollRequest` | schema, sessionId/fence, sequence, immutable requestId, fixed AgentHealth availability/readiness codes and local unresolved-evidence count. |
| `AgentPollReceipt` | same session/fence/sequence, server acknowledgement time, lease expiry, nextPollSeconds and `work = None`. No dispatch union in foundation. |
| `AgentOperationKey` | local contextId/operationId, ordinal 1–4, fence and payload digest; synthetic journal evidence only in #28. |

Pure Application contracts use `AgentActor(Issuer, Subject)`, `AgentLifecycleState`, `AgentCredentialState`, `AgentControlError` and the closed authority policy specified in F1. Api maps wire DTOs to these records. `AgentReadiness` distinguishes ReadyForControlPlane, Unavailable and ActionRequired; execution capability is Unknown/Unavailable until separate native proof. No Windows SID is introduced into Domain/Application; the Windows proof artifact owns those details.

### Human routes (JWT + exact operator + resource authorization)

- `GET /agents/{id}`: safe current health/version/lifecycle, no public/private credential body.
- `POST /agents/pairing-grants`: providerId, expected ProviderInstance version, optional existing agentId/expected agent version for explicit re-enrollment, client request UUID. Capture original actor and versions; no arbitrary destination or secret locator. A new request does not silently replace an active binding.
- `GET /agents/{id}/pairings/{grantId}` and `POST .../confirmation`: original authorized actor, current resource context, strong grant ETag/If-Match, explicit matching-phrase confirmation. No automatic confirmation.
- `POST /agents/{id}/revocation`: strong agent If-Match and allowlisted reason, not secret-bearing free text.
- `GET /agents/downloads/windows`: approved artifact metadata; `GET /agents/downloads/windows/{artifactId}` streams the exact catalogue file. Both authorized; no arbitrary filesystem path or token in URL.

Anonymous → 401; authenticated non-operator → 403. Missing mutation precondition → 428; stale → 412; state/claim/context conflict → 409; rate/capacity bounds → 429 with Retry-After; authority unavailable → sanitized 503. Every route reauthorizes the resource. Pairing grant creation is keyed by original actor/client request ID: an ambiguous retry returns the existing safe receipt **without reproducing the hash-only code**. Explicitly request a replacement grant if the first code response is lost; cancel the old pending transition atomically. Never persist the plaintext code just to replay it.

### Agent routes (dedicated HTTPS listener only)

- `POST /agent/v1/pairing-claims`: server-authenticated TLS bootstrap, no client certificate; rate-limited code/hash check and pending exact-pin reservation.
- `POST /agent/v1/pairings/{claimId}/proof` and `GET /agent/v1/pairings/{claimId}`: exact pending/active pin and claim ownership, idempotent proof/status receipts; no work or lease authority.
- `GET /agent/v1/session`: current active credential, safe session/lease facts only, no acquisition or renewal.
- `POST /agent/v1/sessions`, `POST /agent/v1/polls`: current active credential and current agent authorization version plus session fencing. Session authority is agent-control authority, not permission for any provider operation.
- `POST /agent/v1/rotations`, `POST /agent/v1/rotations/{id}/proof`, `GET /agent/v1/rotations/{id}`: current-key request, pending new-key proof, atomic activation and bounded old-key receipt-only recovery.

Do not expose operational routes on Nginx/browser listener, and do not authorize using Host/forwarded certificate headers. Pending/retiring credentials are explicitly denied session/poll/native-admission authority. Kestrel handshake exact-pin snapshot includes only committed candidates; current DB authorization is independent on every RPC, with no certificate-result cache. An unavailable DB denies operations even if a cached TLS connection survives.

### Incarnation acquisition, replay and provider fencing

Use one current agent session, not one session per assignment. Heartbeat reports only agent-local/control-plane facts; it does not report a provider as SQL-ready. Any future provider operation must separately check its exact assignment, ProviderInstance version and source. Assigning multiple providers therefore does not multiply incarnation authority.

On every relevant ProviderInstance configuration/credential version change, atomically suspend its assignment, increment the assigned agent's AuthorizationVersion and invalidate that agent's session authority under the shared lock order. This conservatively fences the whole installation, including any other assignments. Keep immutable receipt facts; denial follows the new authorization epoch. Direct managed-secret replacement must participate through its actual ProviderInstance owner. This narrowly extends existing version-change transactions, never changes ciphertext/key architecture. Pending grants captured under the old epoch cannot activate. Restoring foundation authority requires a new explicit operator pairing/reconfirmation against current versions with a fresh certificate; do not silently accept the new provider version during polling. Before B-07 execution, provider-owned source verification and B-06 unsettled-work guards remain mandatory: pairing alone cannot authorize endpoint rebinding or erase an old source-qualified native ID.

`ConnectAsync` locks/rechecks the active credential, agent epoch, current assignments and stored fence. Database time decides lease expiry. Apply the following ordered decisions:

| Input/context | Durable outcome |
| --- | --- |
| Unknown/revoked/expired credential, suspended context or quarantined agent | Deny before any receipt replay/acquisition. Revocation overrides a previously successful receipt. |
| Same retained boot/connect request and identical digest, still-current authority | Return the stored original connect receipt, even if its original lease has since expired; no new lease/fence or extension. Client must obtain a new request for a new acquisition. Changed digest is a conflict. |
| Expected fence differs from the current fence | Return safe ContextChanged; no acquisition or renewal. Re-read session status and reconcile before a new request. |
| No live lease, no unresolved local evidence and authoritative guard proves no native admission | Increment fence with overflow check; install the requested boot/new session; persist its immutable receipt and lease in one commit. Foundation's guard proves dispatch disabled; future Run integration must prove no admitted call. |
| Same boot, different request while lease is live | SessionAlreadyActive; recover the retained receipt/session rather than create another owner. Do not extend lease. |
| Different boot attempts acquisition while lease is live | ConflictingIncarnation: quarantine readiness, increment authorization epoch and fence further authority, emit audit; no takeover. A process restart first reads safe session status and waits out the old lease using database remaining time/local elapsed tracking. If another process keeps renewing, it cannot bypass quarantine by claiming to be a restart. |
| Lease expired but evidence is missing/corrupt or native admission is unresolved | ActionRequired; no mutation authority or slot release. Later known-ID observation recovery is separately gated by Run/Attempt reconciliation, not inferred from this session table. |

Backend restart reloads this durable state; it does not reset leases or replay grants. Agent restart preserves its prior receipt/evidence, generates one new boot ID and probes current status before acquisition. Quarantined installations require operator investigation and separately authorized settled re-enrollment; no force-unlock endpoint. A privileged clone that is never concurrent remains a documented limitation, not something hostname/boot IDs can authenticate away.

Only one poll may be in flight. A new poll must have sequence `last + 1`; commit its exact request ID/digest, original acknowledgement time, new lease expiry and safe response before returning. Replay of the **latest retained** identical poll returns that immutable response without renewal. An older sequence is StaleReceipt, not reconstructed success; the client reconciles current session status. Changed digest/request ID at the same sequence is a conflict. Retain the original connect receipt separately from the latest poll receipt so subsequent renewals never change either replay's timestamps or expiry. Before any replay, recheck current certificate/agent authority; a receipt is not an authorization token.

### Pairing and rotation protocol

Follow ADR-0007 Issued → Claimed → Proven → Redeemed. The service automatically creates its key/certificate; the helper carries only the transient pasted code through caller-token-checked IPC. After pending pin commit/snapshot publication, prove possession over mTLS and consume the exact nonce once. Display a non-secret confirmation phrase on both screens, derived from fixed schema/claim/grant IDs, nonce and exact DER digest (proposed 64-bit grouped display digest; not an authentication credential). Original operator confirmation plus current versions redeems atomically.

Store code hash only. Persist non-secret local claim/proof IDs before clearing code; identical proof replay returns its original receipt without extending expiry. A different key/claim never steals a grant. Re-enrollment cannot revive a revoked identity or reset an unresolved native obligation. Rotation uses the same versioned transition lock and new-key possession proof; normal rotation requires settled work. Old credential becomes RetiringReceiptsOnly for at most one hour, then Retired; revocation overrides all overlap. Until real Runs exist, the foundation's guard proves native dispatch is disabled rather than pretending a missing Run table establishes settlement. B-06/B-08 must replace that guard with authoritative admission checks before enabling execution.

## 4. Minimum PostgreSQL proposal

Propose **five new tables**, no changes to A-09 ciphertext/key tables or discovery generations. Four store current agent-control facts; the fifth supplies narrowly scoped security audit rather than a telemetry/event platform. Session and rotation receipts fit these rows; no generic command acknowledgement table is added.

| Table | Proposed columns and ownership |
| --- | --- |
| `Agents` | Id UUID PK; DisplayName varchar(200); State varchar(32); Version/AuthorizationVersion bigint concurrency tokens; CurrentBootId/ConnectRequestId/SessionId nullable UUIDs; SessionFence bigint; LeaseAuthorizationVersion nullable bigint; LeaseExpiresAtUtc timestamptz. Immutable retained connect receipt: request digest, accepted time, original lease expiry, receipt authorization version/fence/recovery mode. Latest poll receipt: sequence, request ID/digest, original ACK time/lease expiry/fence/authorization version/next-poll seconds; reset its receipt fields on a new session, not the monotonic fence. Current safe protocol/package/readiness fields; Created/Updated/LastContact/Heartbeat timestamptz; revocation actor/time/reason. No SID/password/host metrics. |
| `AgentCredentials` | Id UUID PK; AgentId FK; DER bytea; DerSha256/SPKI digest bytea(32 by check); State; NotBefore/NotAfter timestamps; created/proved/activated/retiring-until/revoked timestamps; Version; safe reason. Public material only; no PFX/private key. Unique `(AgentId, Id)` ownership key. |
| `AgentProviderAssignments` | ProviderInstanceId UUID PK/FK; AgentId FK; State; Version bigint; captured ProviderInstance Version bigint; created/updated/revoked actor/time. Unique `(ProviderInstanceId, AgentId)` for grant ownership. One current agent per provider; initial UI creates one assignment per pairing. No universal source/host JSON; later SSIS execution evidence is provider-owned. |
| `AgentEnrollmentGrants` | Id UUID PK; AgentId/ProviderInstanceId and captured versions; Kind Enrollment/Rotation; State; actor issuer varchar(2048)/subject varchar(512) for human enrollment, initiating credential ID for rotation; ClientRequestId; TokenHash nullable bytea(32); ClaimId/ClaimDigest/ClaimedCredentialId; immutable public nonce; ProofRequestId/proved time; confirmation/redeemed receipt/version/timestamps; expiry; ExpectedActiveCredentialId for rotation. Claim/proof/activation receipts are durable fields, not plaintext codes. |
| `AgentSecurityEvents` | UUID PK; AgentId FK; optional same-agent credential reference; event enum, original human actor or authenticated credential, database timestamp, safe reason, old/new authorization/fence versions, bounded correlation UUIDs. Append-only allowlisted facts, not raw bodies/exception text or arbitrary JSON. Grant correlation is an ID, not a retention-blocking ownership FK. |

### Constraints and indexes

- Nonempty IDs, bounded states/text/DER (1–16384 bytes), 32-byte digests, positive checked versions/fences, nonnegative sequences, valid timestamp ordering and explicit nullability/state combinations. All additions fail before bigint overflow; never wrap a fence.
- Restrictive AgentId FKs. Composite `(AgentId, ClaimedCredentialId)` and `(AgentId, ExpectedActiveCredentialId)` enforce credential ownership. Composite assignment ownership prevents a grant using another provider/agent pair. No FK cycle/current-credential pointer is needed: the unique active credential plus agent authorization version is authoritative.
- Unique DER digest across credentials, followed by exact DER comparison: same certificate cannot enroll to two agents. A digest conflict with unequal bytes is a safe collision failure, never replacement. Retain revoked pin tombstones while they can be referenced; re-enrollment uses a fresh key/certificate.
- Partial unique active credential per AgentId. Partial unique live grant per AgentId and per initial ProviderInstance assignment (states Issued/Claimed/Proven); Kind/TokenHash/actor/initiator checks distinguish bootstrap enrollment from authenticated rotation. Expired grants are transitioned under lock before replacement; no volatile `now()` in an index predicate.
- Unique actor/client-request receipt for enrollment and initiating-credential/request receipt for rotation; unique non-null ClaimId/ProofRequestId. Proof and receipt replay bind exact digest/context; SQL uniqueness alone does not perform that authorization.
- Lease all-or-none check for boot/session/lease authorization/time and complete connect receipt fields; latest poll receipt is all-or-none and belongs to that session. Session authorization must be current at acquisition/renewal. Revocation/rotation/provider changes invalidate live authority without deleting immutable receipt evidence; a stored older receipt epoch never authorizes a replay. Revoked/quarantined agents cannot acquire. Active session uniqueness is one row per agent, not a global fleet slot. Agent readiness never allocates the separate global Run slot.
- Index pending/terminal grants by `(State, ExpiresAtUtc, Id)` and security events by `(AgentId, RecordedAtUtc, Id)`; credentials `(AgentId, State)` and assignment ownership lookups; agent list uses bounded keyset `(Id)`. No full-table polling or unbounded EF tracking.

### Transactions, deletion and cleanup

Use current PostgreSQL time for expiry/receipts, local monotonic elapsed time for outbound deadlines and explicit optimistic Version tokens. The lock order extends the existing order: ProviderInstance → managed secret only if involved → Agents (ordered UUIDs) → assignment/credential → grant → audit; B-06 adds Run/Attempt locks after this shared context. No database transaction spans TLS, Windows IPC, SQL or file flushes. All code paths must use the same order.

Grant issue/claim/proof/activation, rotation/revocation and session takeover are short locked CAS transactions. They recheck original actor/resource context and captured versions. Emit security audit in the same commit. Update handshake snapshots only from committed public pins; a missed update can reject a new credential temporarily but cannot authorize it. Reload from PostgreSQL on restart; never recover authorization from local installer metadata.

Provider deletion currently commits discovery retirement before final deletion. Add a **narrow pre-retirement guard under its first provider lock**: refuse deletion while an assignment/grant must remain or unresolved agent/execution evidence exists. Do not wait for a late FK failure after inventory retirement. Test owned-secret lifecycle and unchanged discovery state on rejection. Configuration/credential replacement uses the atomic assignment suspension/agent-epoch rule above, including direct owned-secret replacement; do not send discovery passwords to repair agent readiness. The store must re-read the assignment set after acquiring locks; never acquire a ProviderInstance lock after holding an agent lock. Agent-only heartbeat transactions do not lock providers; provider-bound changes and grant activation use the shared provider-first order.

Deletion is conservative: revoke/soft-disable agents, do not cascade provider/credential/agent deletion through audit or unresolved evidence. An explicit reviewed detach/cleanup may remove a settled revoked assignment before provider deletion; no new user-facing delete framework. B-06 must add references/guards for unsettled Runs before execution is enabled. Lease expiry never grants deletion or native slot release.

Proposed retention: terminal pairing/proof receipts 24 hours; current identity/revocation facts retained; security events 90 days unless held for unresolved evidence; retired credential cleanup only after no live grant/session/Run/journal hold and safe tombstone policy review. No automatic permission to erase an uncertain receipt. Cleanup is at most 100 rows per transaction/request-driven maintenance pass, with strict ownership/state rechecks; no background scheduler. Initial B can retain small public revoked-pin tombstones rather than risk reauthorizing an old installation. Bound pending grants and request rate independently; do not purge active state to meet a disk/row quota.

### Migration boundary

After F3 schema approval, generate one EF migration for these five tables and reviewed constraints/indexes; expected isolated fresh schema is five migrations and twelve public tables including EF history. This is a proposal, not a generated migration. Present exact Up/Down/snapshot/upgrade/rollback SQL; guard Down against active pairing/session/credential authority and unresolved operational evidence under a DDL lock. Test fresh/upgrade/repeat/rollback only after isolated PostgreSQL approval, with a new explicit agent-migration guard. Normal development application needs separate approval and is never startup work. Do not alter the committed Gate B migration.

## 5. Windows installer, identity and journal

F2 uses a quoted, versioned Program Files binary path and `NT SERVICE\\FlowPlaneAgent`, not LocalSystem or the interactive operator. Runtime writes are confined to an ACL-protected ProgramData identity/journal area; service cannot modify its binaries/configuration. Installer/helper verifies actual service SID, configured SID type, running user/group token and private-key/file access. Local setup IPC uses caller-token authorization and remote-client rejection, not a user-name string or a broad Everyone pipe ACL.

The service creates a persistent machine CNG RSA-3072 non-exportable key and non-CA clientAuth certificate; prefer TPM provider when present, otherwise documented software-key clone limitation. Keep only the public certificate in backend storage. Private-key ACL and exact key handle—not machine-scope DPAPI alone—provide isolation. Never generate a new key automatically when expected identity/journal is missing or corrupt. Mark ActionRequired and preserve evidence.

Journal proposed format: one exclusively locked append-only file per installation with versioned length-delimited frames, monotonically increasing local sequence, operation/context ID, phase, fence/digest, bounded provider-owned result receipt and SHA-256 integrity chain. Hashes detect corruption/torn records, not a hostile administrator. `Flush(true)`/write-through semantics and atomic same-volume replacement during settled compaction require actual Windows power/process-fault validation. Multiple agent processes cannot concurrently write. A tail that might contain invocation/result evidence is not silently truncated to resume work.

`IAgentOperationJournal` exposes `AppendAcceptedAsync`, `AppendAdmissionAsync`, `MarkMayHaveInvokedAsync`, `RecordResultAsync`, `MarkCorePersistedAsync`, `ReadRecoverySummaryAsync` and `CompactSettledAsync` with exact `AgentOperationKey`/bounded receipt types and CancellationToken. No Execute/SQL/shell callback. F2 exercises these with synthetic data; F5 only sends bounded recovery counts/digests. B-06/B-08 later supply actual admitted operation and Core-persisted evidence. A failed pre-call write forbids invocation; a failed post-call result write is uncertainty, never a retry. Proposed journal quota 64 MiB with reserved recovery space and at most 4 KiB per frame; full storage fails readiness, never deletes unresolved frames.

Install/reinstall/uninstall is explicit: reject unexpected pre-existing service identity/path; preserve current key and unresolved journal; block destructive uninstall cleanup until evidence is settled and separately approved. Uninstall stops/removes only this service, does not cancel SQL, revoke by guessed hostname, clear Core capacity, remove shared roots or touch PostgreSQL volumes. Re-enrollment/reinstall uses existing protected identity or an explicitly reviewed recovery path, not silent reset.

F6 publishes win-x64 self-contained package plus helper, static reviewed installer/uninstaller, manifest (version/commit/protocol/RID/file hashes) and SHA-256 over exact download bytes. Do not add an automatic updater. Build artifacts stay outside Git; SQL probe binaries/templates are excluded from the release archive. Authenticate UI download and stream one allowlisted artifact ID from a read-only backend catalogue, not an arbitrary path. Human/installer verifies hash before execution. Report Authenticode status honestly; unsigned development acceptance requires explicit approval and must not be represented as production publisher authentication. Production certificate/signing operations are separate readiness work.

## 6. Phase tasks and RED/GREEN gates

Every phase starts with named failing tests, records the actual failure, implements only the stated surface, reruns focused/regression tests and stops for independent review. No test listed here has been executed by writing this plan.

### F1 — First bounded coding task: closed protocol and pure policy

**Create exactly these source/test files initially:**

- `src/agents/FlowPlane.Agent.Protocol/FlowPlane.Agent.Protocol.csproj`
- `src/agents/FlowPlane.Agent.Protocol/AgentProtocol.cs` — schema/version/size constants, shared typed safe codes.
- `src/agents/FlowPlane.Agent.Protocol/PairingMessages.cs` — typed code-bearing classes with redacted representations and safe receipts.
- `src/agents/FlowPlane.Agent.Protocol/SessionMessages.cs` — connect/poll/health/NoWork messages; no executable command type.
- `src/agents/FlowPlane.Agent.Protocol/AgentProtocolCodec.cs` — strict codecs, deterministic public-context fingerprints and lossless Int64 handling.
- `src/backend/FlowPlane.Application/Agents/AgentLifecycleContracts.cs` — provider-independent states/contexts/errors.
- `src/backend/FlowPlane.Application/Agents/AgentLifecyclePolicy.cs` — pure legal transitions and authority decisions.
- `tests/unit/FlowPlane.Agent.Tests/FlowPlane.Agent.Tests.csproj`
- `tests/unit/FlowPlane.Agent.Tests/AgentProtocolCodecTests.cs`
- `tests/unit/FlowPlane.Agent.Tests/AgentLifecyclePolicyTests.cs`

**Modify:** `FlowPlane.slnx`, `tests/unit/FlowPlane.Architecture.Tests/ProjectDependencyTests.cs`. Do not modify Domain, Api registration, Infrastructure, dependencies or B-02 fixtures in F1. Use the existing test package versions, not new libraries.

**Interfaces:** `AgentProtocolCodec.DecodePairingClaim(ReadOnlySpan<byte>) -> PairingClaimRequest`; `DecodePoll(ReadOnlySpan<byte>) -> AgentPollRequest`; `EncodePairingReceipt(PairingClaimReceipt) -> byte[]`; `AgentLifecyclePolicy.Authorize(AgentAuthorityContext, AgentControlAction) -> AgentPolicyDecision`. Define `AgentAuthorityContext(AgentLifecycleState AgentState, AgentCredentialState CredentialState, bool IsCurrentAuthority, bool CertificateWithinValidity)` and `AgentPolicyDecision(bool Allowed, AgentControlError Error)` as closed records. States: agent Pending/Active/Quarantined/Revoked; credential PendingProof/Active/RetiringReceiptsOnly/Retired/Revoked. Actions: PairingProof, PairingStatus, Connect, Poll, RotationReceipt. Error values: None, InvalidCredential, Revoked, ActionRequired, ContextChanged, OperationNotAllowed. Unknown numeric enums reject.

The pure policy is an authority matrix, not a session/grant store: invalid lifetime or revoked state denies every action; quarantined denies control authority; pending credentials permit only their scoped proof/status; retiring credentials only their scoped rotation receipt; active/current credentials permit the matching control actions. F3/F4 must establish exact resource ownership/current context before invoking it, then recheck inside the mutation transaction. A boolean in a pure test is not evidence of actual TLS/DB validation. Grant races, immutable receipt replay and session CAS remain F3 tests, not simulated F1 persistence. Certificate verification/nonce generation belong to F2/F4.

- [ ] RED: `Unknown_schema_or_member_is_rejected`, `Oversize_or_deep_message_is_rejected`, `Int64_fence_roundtrips_losslessly`, `Pairing_code_is_not_in_ToString`, `Pending_or_retiring_identity_cannot_poll`, `Pending_proof_has_no_control_authority`, `Revocation_overrides_old_authority`, `Expired_or_stale_authority_is_denied`, `Unknown_action_is_rejected`. Assert exact action/error decisions above, lossless value `9223372036854775806`, 64-KiB body/16-KiB DER bounds and depth 8.
- [ ] Run `dotnet test tests/unit/FlowPlane.Agent.Tests/FlowPlane.Agent.Tests.csproj --configuration Release --nologo -m:1`; capture meaningful missing-contract/behavior failure, not an unavailable dependency/environment failure.
- [ ] GREEN: implement the signatures/types/strict codec only. Reject unknown/invalid input, model no native completion or service readiness not actually established, redact code-bearing representations. Do not build a service or persistence store.
- [ ] Rerun focused tests; run `dotnet test tests/unit/FlowPlane.Architecture.Tests/FlowPlane.Architecture.Tests.csproj --configuration Release --nologo -m:1`, normal Release build and format verification. Review exact new dependencies and all-file inventory. Stop for review, no automatic commit.

### F2 — Windows host and local durability

**Files:** Create the Runtime/Windows/Setup projects above; focused `Runtime/AgentPollLoop.cs`, `Journal/AgentOperationJournal.cs`, `Identity/WindowsAgentIdentity.cs`, `Security/ServiceTokenVerifier.cs`, `Setup/LocalSetupPipe.cs`, `Setup/WindowsServiceInstaller.cs`; add portable journal tests and Windows-specific tests. Define `IWindowsAgentIdentity.LoadOrCreateAsync` (creation only explicit install), `IServiceTokenVerifier.Verify`, and `ILocalSetupChannel.PairAsync` with transient input and safe receipt. No networking to SQL or dynamic executable dispatch.

- [ ] RED/GREEN: normal journal reopen, abrupt process kill at each frame/flush, lost acknowledgement, corrupt/truncated tail, full disk/write/flush/compaction failure, exclusive writer, no secret payload, duplicate ID/same digest replay versus changed digest/reordered phase rejection.
- [ ] RED/GREEN on Windows: wrong runtime SID, ordinary-user pipe access, remotely opened pipe, wrong key ACL, missing existing key, service-config mismatch and unresolved-evidence uninstall fail safely; actual token can read/use its key and journal but not write Program Files/config.
- [ ] Cross-publish produces a package only. Stop before runtime install/start. After separate Windows authorization, certify the minimal service checkpoint and approve a separate B-02-only probe implementation/window. No dependency on enrollment, B-07 or B-08 is introduced.

### F3 — Durable lifecycle store and schema gate

**Files:** Create `Application/Agents/IAgentControlStore.cs`, `Infrastructure/Persistence/Agents/AgentRecords.cs`, `AgentModelConfiguration.cs`, `AgentControlStore.cs`, `AgentDatabase.cs`; modify DbContext/DI and the narrow ProviderInstanceStore pre-retirement guard. `IAgentControlStore` supplies `IssueGrantAsync`, `ClaimAsync`, `RecordProofAsync`, `ConfirmAsync`, `RevokeAsync`, `ConnectAsync`, `PollAsync`, `BeginRotationAsync`, `ActivateRotationAsync`, `CleanupAsync`; requests carry captured versions and actor/certificate/session context, receipts are safe immutable facts. Certificate/actor validation occurs before the store and versions are rechecked inside the transaction; I/O is never held under DB locks.

- [ ] RED/GREEN pure model checks and faultable coordinator/store-double tests first; then review the five-table EF model and generated migration/SQL before application.
- [ ] After isolated migration authorization, real PostgreSQL tests prove same-provider/composite ownership, unique grants/pins/active credentials, two-actor races, enrollment/rotation/revocation races, conflicting claim rejection, duplicate proof replay, durable receipt-before-response, lost commit reconciliation, strict expiry/overflow, every Connect decision-table branch and early provider-deletion rejection preserving discovery/secrets.
- [ ] Replay the original connect after several polls and assert its timestamps/expiry remain byte-for-byte original. Replay only the latest poll without extension; reject older/changed polls. Race two expired-lease acquisitions; exactly one can acquire, the other must observe the changed fence. Race provider configuration/direct-secret replacement against acquisition/poll/grant confirmation; obsolete epochs never gain authority and suspension is atomic.
- [ ] Fresh/upgrade/repeat/guarded rollback, index/check/FK introspection, bounded cleanup and JSON-free identity fingerprint stability are required. EF InMemory and skipped DB tests are not database evidence.

### F4 — Real TLS and pairing boundary

**Files:** Api `Agents/AgentListener.cs`, `AgentCertificateAuthentication.cs`, `AgentPairingApi.cs`, `AgentControlApiContracts.cs`; Infrastructure committed pin snapshot loader; minimal frontend `agents/AgentPairing.tsx`/client/tests. Wire only after schema test approval. Reuse existing operator auth and lossless DTO conventions.

- [ ] RED/GREEN: unknown/self-signed/no-EKU/expired/revoked/same-key-different-DER certificates; strict server SAN/chain; wrong listener; forged certificate/forwarded-port headers; pending proof/status versus active session access; expired/substituted/replayed code; exact original actor/phrase/version confirmation. No plaintext code replay after lost creation response.
- [ ] Use real loopback Kestrel/TLS with ephemeral synthetic test certificates and explicit test trust, not WebApplicationFactory alone. Do not install trust or change development PFX. Race revocation over an already-established connection and verify uncached DB denial. Pin snapshot cannot override DB authority; rebuild it after restart.
- [ ] Suppress raw certificate-handler failure/subject/exception logging and request bodies at this boundary; emit only allowlisted codes. A malicious presented certificate can itself carry sensitive/control-character subject text. Canary subjects, malformed DER and enrollment failures must not leak through framework diagnostics. Certificate subject text never contributes authority.
- [ ] Synthetic browser tests cover code clearing/no storage, explicit phrase confirmation, 401/403, stale ETag and lost network responses. Disable credential traces/HAR/video/screenshots. Real local development HTTPS/trust and Windows pairing require separate provisioning approval.

### F5 — Polling, rotation and recovery

**Files:** Runtime polling/session/recovery state, Api agent-session routes, Infrastructure session/rotation methods; existing health UI receives only approved safe fields. Initial lease 60 seconds, renew via heartbeat/poll at most 15 seconds; idle polls 5 seconds with jitter and max one in-flight request. DB time is authority; local elapsed timers bound waits. Use the explicit acquisition/replay table above, retaining original connect and latest poll receipts. A restarted process probes/waits rather than assuming graceful shutdown released authority. Expired owners reconnect via atomic fenced recovery, not silent renewal.

- [ ] RED/GREEN: lost connect/heartbeat/proof/rotation responses; backend and agent restart; duplicate/reordered polls; unsupported version; false self-reported readiness; concurrent incarnations; revoked open connection; expired lease. NoWork never becomes a callable arbitrary payload.
- [ ] Two-sided rotation proves new key, CAS activates exactly once, old key becomes receipt-only then retired, revocation wins, unsettled-operation guard fails closed. No lease expiry releases a Run slot. After B-06/B-08 exist, require integrated revocation/unsettled-native tests before dispatch is enabled; no claim those native scenarios passed in foundation.

### F6 — Package and operator acceptance

**Files:** reviewed static packaging/install/uninstall scripts under `deploy/agent/windows/`; Api download catalogue; frontend AgentSetup/AgentHealth components; Windows/package/browser tests. No new generic router/framework or hosted service beyond the Windows Worker and existing backend.

- [ ] RED/GREEN: wrong hash/modified archive, unsupported RID/version, path traversal, wrong download authorization, secrets/probe artifacts in archive, existing mismatched service, uninstall with unresolved journal, logout/provider switch and stale pairing clearing.
- [ ] Both development and static Nginx paths preserve JWT/operator boundary, same-origin human API routing and Node-free production frontend. Agent traffic stays on its own HTTPS listener.
- [ ] Independent security review and separately approved Windows install/pair/restart/rotate/revoke/uninstall runtime acceptance. Clearly distinguish synthetic tests, real TLS/DB, real Windows and B-02 native proofs. No skipped runtime tests counted as passing.

## 7. Security acceptance matrix

| Case | Required evidence | Owner |
| --- | --- | --- |
| Unknown/revoked/expired certificate, no EKU, DER substitution | Real handshake + application current-DB denial, including reused connection | F4/F5 |
| Invalid server chain/SAN or HTTP/redirect | Agent refuses before transmitting code/authenticated request | F4 |
| Code replay, wrong phrase, other actor/key, concurrent grants | Exactly one bound claim/activation or safe conflict; never an operational pending identity | F3/F4 |
| Cross-agent/provider/session/fence | PostgreSQL ownership and per-RPC auth fail closed; no safe receipt leaks to wrong context | F3/F5 |
| Wrong service account/SID/ACL/IPC caller | Actual Windows token/CNG/file/pipe denial; SCM output alone insufficient | F2 |
| Duplicate/reordered synthetic operation evidence | Immutable same-body receipt replay; changed/order conflict; zero execution callback | F1/F2 |
| Kill/commit ACK loss and restart | Durable identity/claim/session/journal reconciliation; no mutation replay | F2–F5 |
| Journal corruption/full disk/missing expected key | ActionRequired/Unavailable and preserved unresolved evidence | F2 |
| Revocation while native work unsettled | Synthetic guard now; actual Run/Attempt integration required in B-06/B-08; admitted SQL not magically cancelled | F3/F5 and later B-08 |
| Install/uninstall/reinstall | Correct account, integrity and retained unresolved evidence; no key regeneration or native cancellation | F2/F6 |
| Secrets in installer/logs/responses | Synthetic canaries absent except authorized one-time code response; redacted ToString, body logging off, no key/PFX/SQL password/probe payload in archive | F1/F4/F6 |

## 8. Real runtime and delivery gates

1. Approve this implementation plan and F1 only; write no Windows code merely because architecture is Accepted.
2. Review F2 code/package and authorize exact Windows installation/SID/ACL scope; no SQL grants/native operations implied.
3. Independently approve B-02 A interactive proof if desired. After minimal F2 service checkpoint, separately approve B-02 B harness, intended account/login/grants and each native phase. A alone is never service feasibility.
4. Review F3 generated migration before application; then approve isolated PostgreSQL tests, not normal development migration. Do not run volume-deleting wrappers.
5. Review exact development HTTPS address/SAN/trust/secret mounts before modifying runtime configuration; use distinct TLS material and preserve existing Data Protection root. No HTTP fallback.
6. Authorize real Windows pairing/restart/key-rotation/revocation/uninstall tests after synthetic, DB and TLS gates; no package execution in foundation acceptance.
7. Human commit/CI gates precede any proposed #28 closure. B-07/B-08 stay separately scoped. No production monitoring or broader provider/runtime certification is implied.

## 9. Verification commands for later approved implementation

For each phase, run the focused named tests first and then applicable existing regressions. Expected results are proposed acceptance criteria, not results of this planning turn:

```sh
dotnet restore FlowPlane.slnx
dotnet build FlowPlane.slnx --configuration Release --no-restore -warnaserror
dotnet test tests/unit/FlowPlane.Agent.Tests/FlowPlane.Agent.Tests.csproj --configuration Release --nologo -m:1
dotnet test tests/unit/FlowPlane.Architecture.Tests/FlowPlane.Architecture.Tests.csproj --configuration Release --nologo -m:1
dotnet test FlowPlane.slnx --configuration Release --no-build --no-restore --nologo -m:1
dotnet package list FlowPlane.slnx --vulnerable --include-transitive --format json
dotnet format FlowPlane.slnx --verify-no-changes --no-restore
sh tests/documentation/foundation-contract.sh
git diff --check
```

Windows test projects/solutions and gated PostgreSQL commands are added only by their approved phase; do not invoke guessed paths or missing migration guards now. Preserve the 18 B-02 files and all Accepted ADRs. The next bounded task is F1's closed protocol/pure policy RED/GREEN cycle, not service installation or SSIS execution.
