# A-12 Stage 2 — durable discovery hosting (Gate B)

> **For agentic workers:** Use `superpowers:executing-plans` and RED/GREEN checks. The approved Stage 2 request is the specification. Gate B application is authorized only in newly created, disposable PostgreSQL 18 test databases. Do not commit or migrate the normal development database.

**Goal:** Admit one durable, idempotent discovery refresh and execute its existing inventory generation in the backend.

**Architecture:** Extend inventory generations with receipt/ownership fields rather than creating a second refresh claim. PostgreSQL reserves one global hosted slot; the hosted service claims an Accepted generation once. Existing streamed staging and atomic publication remain authoritative.

**Tech stack:** Existing .NET 10, EF Core/Npgsql/PostgreSQL 18, ASP.NET Core BackgroundService and xUnit. No new dependencies.

## Constraints and decisions

- Work in the authoritative main checkout, as requested; preserve the approved baseline and never commit automatically.
- Gate B passed independent static review and is approved for isolated PostgreSQL 18 acceptance only. No normal-development migration, SSISDB mutation, real credential onboarding or root-key changes.
- Receipt ID is the canonical UUIDv7 Idempotency-Key, globally unique. First admission accepts a timestamp within five minutes of PostgreSQL time. Replay is retained for seven days and compares exact issuer, subject, provider and If-Match version.
- No browser generator is added before the UI stage. Its contract is RFC 9562 UUIDv7, not `crypto.randomUUID()`; server tests use .NET's UUIDv7 implementation and independent timestamp/variant boundary cases.
- One supported backend replica. One nullable execution-slot value (1) has a partial unique index. Accepted work reserves capacity, so there is no queued backlog.
- Accepted work can be started once. Interrupted started scans are abandoned/fenced, never resumed or automatically scanned again.
- Lease loss prevents staging/publication. A network-isolated old process may retain native IO temporarily; leases do not promise instantaneous termination of external SQL requests.
- One-hour minimum derived inventory deadline is retained. Lease renewal never extends the overall deadline.
- Runner defaults disabled until Gate B is approved/applied. Base boot remains independent of SSIS and new schema availability. Admission while disabled returns a safe unavailable response.

## Implementation tasks

### 1. Contracts and admission validation

- [x] RED: new routes authenticate/authorize; absent UUIDv7 policy fails.
- [x] GREEN: narrow Application receipt/store contracts, UUIDv7/skew policy and runner options.
- [x] Test malformed keys, exact actor binding, skew bounds, invalid lease settings and redaction.

### 2. Durable generation admission and execution

- [x] Add Accepted and generation receipt/ownership fields; use short transactions with global admission lock then ProviderInstance → managed secret → state → generation.
- [x] Implement replay, slot reservation, accepted-generation acquisition, lease renewal, context verification and expiry reconciliation (PostgreSQL acceptance remains gated).
- [x] Add owner/lease checks to existing staging/publication fences, preserving legacy non-hosted refreshes.
- [x] Unit checks: execute an existing session without BeginRefreshAsync; incomplete/uncertain publication and cancellation retain truthful outcomes.

### 3. API and hosted runner

- [x] Add authenticated admission/status routes, If-Match and exact operator/endpoint rechecks.
- [x] Execute in independent scopes without HTTP contexts/tokens/password capture; await work and lease loop, no detached Task.Run.
- [x] Test authorization, policy denial, receipt responses and recovery orchestration with controlled external boundaries; durable replay/conflict verification is pending PostgreSQL.

### 4. Gate B and review

- [x] Generate one additive migration, snapshot and upgrade/rollback SQL; do not apply them.
- [x] Add gated PostgreSQL tests for durable receipt/slot/concurrency/recovery, explicitly pending approval.
- [x] Restore, Release build, non-database tests, architecture, audit, format and documentation checks.
- [x] Fresh independent whole-change review and one correction pass; report remaining findings and exact diff. PostgreSQL acceptance remains unperformed.

## Review focus

1. Lost HTTP acknowledgement must replay a committed receipt without creating another generation.
2. A lease-expired or restarted executor must not resume staged scans or publish obsolete work.
3. Provider/credential replacement before secret use must not mix a new password with an old endpoint.
4. Publication acknowledgement uncertainty must be reconciled by refresh ID, never blindly aborted.
5. Defaults must preserve foundation boot while Gate B is unapplied, with no background schema writes.

## Verification boundary

Database transaction, crash/restart and constraint acceptance use isolated PostgreSQL 18 after explicit Gate B approval. Unit doubles are orchestration evidence only, not proof of PostgreSQL concurrency. No skipped integration test counts as acceptance. The post-migration verification record below is separate from the earlier static-review gate.

## HTTP contract

Both routes inherit Stage 1's authenticated, exact issuer/subject operator policy and `Cache-Control: no-store`.

| Backend route (Nginx adds `/api`) | Contract |
| --- | --- |
| POST `/provider-instances/{id}/discovery-refreshes` | Empty body; one strong ProviderInstance `If-Match` and one canonical lowercase RFC 9562 UUIDv7 `Idempotency-Key`. Returns 202 only after generation, receipt and slot reservation commit, with browser-facing Location and Retry-After. Replays return the same receipt. |
| GET `/provider-instances/{id}/discovery-refreshes/{refreshId}` | Resource authorization and current endpoint/type/enabled checks on every read; 200 safe receipt or 404. No native scan or inventory payload is returned. |

Receipt fields: `refreshId`, `providerInstanceId`, `status`, `acceptedAtUtc`, `expiresAtUtc`, `observations`, `candidates`, `scanCompleted`, and optional safe `error`. Counts describe durable provisional staging until Published, not a completed or authorized inventory. No actor, executor, endpoint, credential, locator, token, raw native error or metadata document is returned.

Missing If-Match is 428; malformed input/key is 400; stale admission version is 412; different key context or occupied slot is 409; unavailable/disabled hosting is 503. Storage/validation failures use the existing sanitized Problem Details boundary. Existing receipts compare original exact issuer, subject, ProviderInstance and version before replay; changing a precondition does not create another scan.

## Lifecycle and recovery

| Durable phase | Worker/recovery behavior |
| --- | --- |
| Accepted | Receipt and global slot are committed, but no native scan has started. Reservation lasts through the derived operation deadline; no executor renewal is needed before acquisition. An independent worker scope can acquire it once after restart while that deadline is live. |
| Scanning | Existing generation moves to Staging with executor ID; all rows are provisional. No second BeginRefreshAsync call is made. |
| Preparing | A-11b performs bounded comparison and complete-summary checks. |
| Publishing | Ready generation has a durable publication-attempt marker; the existing short atomic publication transaction checks context and fencing. |
| Published | Durable PublishedAtUtc and the current-generation pointer committed together. Lost acknowledgement cannot reverse that fact. |
| Failed / Unavailable | Definitive pre-publication failure; previous complete generation is preserved. Native Unavailable remains distinct from definition incompatibility. |
| Abandoned | Expired/interrupted or superseded work is fenced. A new user request needs a new key; staged scans are never resumed. |
| PublicationUncertain | Receipt retains capacity until reconciliation. Recovery locks the state to wait out any publication transaction and inspects the durable refresh ID. It either confirms publication or fences a demonstrably unpublished generation; a failed acknowledgement alone is never a failure verdict. |

The execution slot is nullable integer 1 with a partial unique index across all ProviderInstances. Admission/recovery additionally serialize through a transaction-scoped PostgreSQL advisory lock, followed by ProviderInstance → managed secret → discovery state → generation locks. The existing per-provider active index now includes Accepted. HTTP disconnection does not own the worker token or abort a committed acceptance.

The executor uses independent DI scopes and an awaited renewal loop, retaining neither HttpContext nor bearer tokens. A resolved password stays inside the provider callback. Persisted provider/managed-secret context and executor lease are checked after resolution but before native IO; existing staging/publication fences also check executor identity and lease. Every bounded staging transaction revalidates the claim. Normal key/credential replacement fences obsolete publication.

The inventory deadline remains the maximum of one hour and claim + scan + preparation + publication budgets. PostgreSQL time establishes expiry; the acquired remaining budget bounds local execution through CancellationTokenSource's elapsed-time timer. Renewal is capped by that deadline and cannot revive an expired owner. One backend replica is supported; database fencing protects publication if an obsolete process survives, but does not promise instantaneous cancellation of external IO under unsupported split-brain deployment.

Crash boundaries:

- Before 202: a rollback leaves no receipt; a committed but unacknowledged admission is replayed by key.
- After 202 before start: the durable Accepted row can be acquired once after restart, if still live; otherwise it is abandoned.
- During scan/preparation: lease expiry fences and abandons, never resumes or automatically retries the scan.
- During publication: recovery waits on the state lock and reconciles committed data by refresh ID.
- After publication commit before acknowledgement: Published remains authoritative and recovery releases the retained execution slot.

Terminal receipts are retained for at least seven days. Existing bounded inventory cleanup removes payloads separately and can remove unreferenced expired terminal receipts; current/last-attempt provenance can remain longer. No unbounded backlog or native-history browser is introduced.

An idle hosted poll selects at most one ProviderInstance with obsolete payloads/expired unreferenced receipts, then invokes one existing cleanup batch with a maximum five-second cancellation budget. It releases the admission transaction/lock first. Current and in-flight generations remain protected under the cleanup state lock; subsequent polls continue large cleanup jobs without another service or queue.

## Configuration and Gate B operation

`FlowPlane:DiscoveryHosting` binds standard backend configuration, including environment variables:

| Setting | Default | Validation / environment name |
| --- | --- | --- |
| Enabled | false | `FlowPlane__DiscoveryHosting__Enabled` |
| LeaseSeconds | 60 | 15–300; `FlowPlane__DiscoveryHosting__LeaseSeconds` |
| RenewalSeconds | 15 | 1–LeaseSeconds/3; `FlowPlane__DiscoveryHosting__RenewalSeconds` |
| PollSeconds | 2 | 1–LeaseSeconds/3; `FlowPlane__DiscoveryHosting__PollSeconds` |

Unknown settings or invalid values fail startup clearly. `FlowPlane:Operators`, `FlowPlane:Ssis:AllowedEndpoints`, TLS policy and the A-11 scan options retain their existing bindings. Configure backend-only values through environment or a private Compose override; restart to apply policy changes. Do not enable hosting before Gate B application is separately approved. Disabled hosting performs no runner database access and keeps base startup independent of SSIS.

Gate B `20261026084710_DurableDiscoveryHosting` changes only ProviderDiscoveryGenerations: eight additive ownership/receipt columns, hosting checks, the global hosted-slot unique index, and Accepted in the existing status/claim policy. No tables or A-09 schema are added/altered. Four migrations and seven public tables are expected after application. The A-11b publication check and current-generation relationships remain unchanged.

Generated rollback first refuses active hosted slots, Accepted rows, or any `PublicationUncertain` receipt, including terminal rows without a slot. A separately approved rollback must stop/quiesce Stage 2, reconcile all owners and verify backups before dropping receipt/lease fields. Rollback loses hosted idempotency/actor information; it must not be performed while Stage 2 serves requests. No automatic startup migration is introduced.

`FLOWPLANE_TEST_DISCOVERY_HOSTING_MIGRATION_APPROVED=true` is the additional isolated-test application guard. The controlled A-09 verifier supplies it alongside its existing inventory approval, while still rejecting externally redirected configuration. A-05 invokes EF migrations directly and does not inspect this test-helper flag; its expected count is now four migrations. Neither volume-deleting wrapper was executed against existing development resources for this gate. Isolated database acceptance is not execution of the exact Foundation PostgreSQL wrapper/workflow; that workflow still requires verification after review, commit and push. Nothing here authorizes changing the normal development database.

## Execution ledger

The entries below record the original pre-application gate; the subsequent isolated acceptance is recorded separately afterward.

- Baseline verified: main `ffd3b28`, clean tree, Foundation run 40183072804 successful, issue #13 open.
- RED: new HTTP routes returned 404 rather than 401; UUIDv7 admission policy absent. GREEN: protected routes, bounded timestamp/variant/canonical checks and disabled-hosting behavior passed.
- RED: native Unavailable was flattened to IncompleteScan. GREEN: accepted executions preserve the unavailable category without changing legacy refresh/publication policy.
- Runner/HTTP tests exercise awaited admission, independent scopes, existing-session execution, revoked actor/endpoint denial, cancellation on lease loss, sanitized failures and uncertain-result preservation. They do not replace PostgreSQL acceptance.
- PostgreSQL tests cover durable replay, cross-provider slot contention, once-only acquisition after restart, expired scan/preparation/publication fencing, compatible Partial/empty publication, configuration changes, and real transaction fault injection before/after publication commit. All remain gated and pending.
- Ruling: retain the existing checkout and no automatic commits/worktrees, consistent with the authoritative-main review workflow.
- Ruling: an unstarted Accepted reservation lasts until the derived operation deadline, not the shorter executor lease. A backend outage must not discard durable unstarted intent merely because no executor could renew it; acquisition starts the rolling lease. The gated PostgreSQL restart test checks this distinction.
- Independent source review found two Important defects: the hosted path omitted cleanup, and recovery trusted an unlocked lease snapshot. One correction pass adds bounded idle cleanup and rechecks the locked row's lease, slot, status and overall deadline. Recovery policy RED reproduced six wrong outcomes; GREEN passed seven cases. The real PostgreSQL renewal-interleaving and obsolete-payload cleanup regressions are written but remain pending Gate B approval.
- Declined-to-judge rulings: PostgreSQL constraint/interleaving/rollback and real process/network crash evidence require the post-migration gate; no acceptance is inferred from skipped tests. Live identity-provider/browser evidence remains a later A-12 acceptance gate; signed-JWT HTTP tests are the current boundary.

## Post-migration isolated acceptance

- Rollback RED reproduced a slot-null terminal receipt with unresolved publication being allowed through the original guard. GREEN rejects uncertainty independently of slot ownership. A second real PostgreSQL interleaving reproduced admission after the guard but before receipt/ownership DDL; the guard now holds an ACCESS EXCLUSIVE table lock through the rollback transaction. Full rollback regressions also preserve active Accepted/Staging/Ready receipts, executor fields and fences after rejection. Only the existing unapplied Gate B `Down` guard changed; upgrade SQL, schema and snapshot did not change in this correction.
- Real PostgreSQL 18 tests passed fresh four-migration bootstrap, SQL/EF upgrades from the committed A-11b schema with published/terminal data, repeat application, all eight column types/defaults, ownership/lease checks, direct global-slot uniqueness and quiescent rollback preserving A-11b data. Generated SQL and EF catalog definitions agree. Every database was newly created and disposable; the normal development database remains at three migrations.
- Hosted PostgreSQL acceptance includes cross-provider and same-key admission races; original actor/provider/version replay binding; database-clock UUIDv7 skew; lease renewal/recovery interleaving; direct managed-secret/configuration fencing; bounded idle cleanup; and publication commit fault reconciliation. A real HTTP test holds the admission transaction before commit, proves no 202 or externally visible receipt, then verifies durable replay and exactly one runner scan/generation.
- Actual subprocess SIGKILL tests cover Accepted, Staging, Ready and Published boundaries without graceful shutdown. Unstarted accepted work is acquired once; started scans are fenced/abandoned, never resumed; committed publication survives lost completion acknowledgement. Preparing expiry is additionally tested through controlled durable state. These tests do not claim exhaustive packet-loss or split-brain certification.
- Final native WSL Release suite: 439 passed, zero failed, zero skipped, including 68 persistence tests and the rollback admission-interleaving regression. The A-10/A-11a/A-11b real suites each passed six cases plus one catalog scan, both natively and in the non-root `10.0.402-noble` SDK container against the unchanged SQL Server 2025 fixture. The first container attempt failed in CLI initialization before any tests; using the documented writable HOME resolved it. It is not counted as test evidence.
- Fresh restore with HTTP cache disabled and direct/transitive NuGet auditing found no reported vulnerabilities. Release build, formatting verification, architecture tests, documentation/shell contracts, Compose configuration and diff checks passed.
- Independent final review identified the A-09 wrapper's missing isolated Gate B test approval. The controlled environment now supplies that flag without relaxing target or cleanup checks. Its mock regression failed first with process exit 42, then all 19 fake-command safety tests passed. No destructive wrapper was executed for this gate; exact Foundation CI remains pending commit/push verification.
- Development PostgreSQL/backend identities and health, volume identity, three migrations, counts (zero providers, zero secrets, one protected key), protected key-ring fingerprint and external PFX checksum/permissions were preserved. No SSISDB mutation or real persistent credential onboarding occurred. Independent off-host root-certificate backup remains pending; no normal-development Gate B migration is authorized.
- Review rulings: normal-development migration, live browser acceptance and multiple replicas remain outside this gate. Transaction fault injection/SIGKILL evidence is not a claim about every possible network partition; durable fencing is the implemented protection. The supported Nginx path remains HTTP/1.1 upstream; direct-backend HTTP/2 acceptance is not established here.

## Platform references

- [RFC 9562 UUIDv7 layout](https://www.rfc-editor.org/rfc/rfc9562.html#section-5.7): 48-bit Unix-millisecond timestamp, version and variant checked independently. Server test vectors include .NET's [CreateVersion7](https://learn.microsoft.com/en-us/dotnet/api/system.guid.createversion7?view=net-10.0) and independent skew/malformed cases.
- [ASP.NET Core hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0): scoped services require explicit scopes and unexpected termination need not invoke StopAsync. Recovery therefore uses durable claims rather than shutdown callbacks.
