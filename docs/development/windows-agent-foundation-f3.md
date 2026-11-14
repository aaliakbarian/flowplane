# Agent Foundation F3 — persistence and migration review

Status: isolated PostgreSQL 18 acceptance passed; **normal development migration
application remains unapproved**. F3 is staged for final review, not committed.
Follow [ADR-0007](../adr/0007-windows-agent-security-transport-and-durable-operations.md)
and the [Agent Foundation plan](../superpowers/plans/2026-11-01-windows-agent-foundation.md).
F3 adds no HTTP listener, enrollment transport, polling loop, Windows operation,
native execution admission, Run storage or SSIS adapter. F1/F2 remain unchanged.

## Contracts and authority

`IAgentControlStore` exposes IssueGrant, Claim, RecordProof, Confirm, Revoke,
BeginRotation, ActivateRotation, Connect, Poll, scoped ReadGrant/ReadSession and
bounded Cleanup. These are transactional persistence methods, not HTTP endpoints
or a generic command runner. Application types are provider-independent and have
no protocol/runtime, Windows, SqlClient or certificate-library dependency.

F4 must authenticate the operator/resource and validate TLS possession, signature,
clientAuth profile and certificate trust before constructing certificate context.
The store independently derives the exact raw DER/SPKI SHA-256 digests, checks
RSA-3072, validity, exact DER equality, current state, ownership and captured
versions. A digest alone is not authorization. Pending keys can only participate
in their own claim/proof/receipt lifecycle; retiring keys can only read their own
rotation receipt for at most one hour. Revocation overrides replay and overlap.

An enrollment grant lasts ten minutes. Only a hash of the high-entropy code is
stored. The caller creates the code outside the transaction and must not persist
it. Issuance replay uses original issuer/subject/client-request context and returns
the original safe receipt, never a regenerated code. Losing the first code
response requires an explicit new grant, not an automatic new identity.
Actor-request uniqueness uses a fixed 32-byte length-prefixed digest and then
compares exact issuer/subject; this avoids oversized Unicode btree keys and fails
closed on a digest collision. No secret code, password or private CNG key is stored.

Claim binds one certificate, claim ID and public nonce. Proof binds the exact
claimed key, claim ID, nonce and proof request ID. Operator confirmation rechecks
the original actor and expected proof version. Rotation requires the initiating
active key, a distinct new key, proof by that key, unchanged context and settled
work. Activation, assignment state, authorization epoch and audit commit together.
One active credential and one live grant per agent are database constraints.

## Five-table schema

| Table | Current facts and safeguards |
| --- | --- |
| Agents | UUID identity; lifecycle/version/authorization epoch; monotonic session fence; separate original connect and latest poll receipts; current lease, boot and session; safe protocol/package/readiness, last contact/heartbeat and recovery flag; revocation actor/time. |
| AgentCredentials | Same-agent UUID identity; public DER (1–16384 bytes), 32-byte DER/SPKI digests, validity and lifecycle times; globally unique DER digest; one filtered active key per agent. Public revoked pins are retained. |
| AgentProviderAssignments | ProviderInstance UUID primary key; same-agent ownership; state/version and captured provider/owned-secret versions. One current agent per provider. |
| AgentEnrollmentGrants | Enrollment/rotation state; original actor or initiating key; request digest, nullable code hash; captured agent/provider/assignment/credential versions; immutable claim/proof/redemption receipts and ten-minute expiry. Composite ownership FKs and filtered uniqueness. |
| AgentSecurityEvents | Allowlisted event kind, agent/authenticating credential or original actor, bounded UUID correlations, database time and before/after epoch/fence. No raw request, arbitrary JSON, exception text or telemetry. |

All ownership FKs restrict deletion. Composite credential and assignment FKs reject
cross-agent/provider references. Grant correlation in audit is an ID, not a FK
that prevents terminal receipt retention. Columns use UUID, bigint, timestamptz,
bounded varchar, boolean, integer or bytea, never floating-point native IDs.
No native execution ID is stored by F3. Existing F1 canonical decimal Int64 wire
contracts are unchanged. There is no current-credential FK cycle.

The authoritative definitions are the [migration](../../src/backend/FlowPlane.Infrastructure/Persistence/Migrations/20261111081939_AgentControlFoundation.cs),
[model snapshot](../../src/backend/FlowPlane.Infrastructure/Persistence/Migrations/FlowPlaneDbContextModelSnapshot.cs),
[upgrade SQL](agent-foundation-f3-up.sql) and [rollback SQL](agent-foundation-f3-down.sql).
The isolated latest schema is **five migrations and twelve public tables including
EF history**; development remains at four migrations/seven tables. No existing
A-09 or discovery table/column is altered by Up.

## Transactions, sessions and replay

Lock order: sorted ProviderInstances → owned managed secret when involved →
sorted Agents → assignments/credentials → grants → audit. Database time is read
after locks. No transaction spans TLS, IPC, SQL Server or a filesystem flush.
Row/version checks, unique indexes and partial indexes supplement each other;
in-process tests are not PostgreSQL concurrency evidence.

Agent-only session transactions never acquire a provider lock after the agent
lock. They read current provider/secret versions; a provider update cannot commit
until it fences the locked agent. Configuration and credential updates atomically
suspend the affected assignment, invalidate the whole agent epoch/session and
cancel pending grants. Direct owned-secret replacement locks actual owners and
rechecks ownership. Ciphertext format and root-key/Data Protection design do not
change. Provider deletion is rejected under its first provider lock **before**
discovery retirement when agent evidence still refers to it; no late FK failure
is used as the pre-retirement guard.

The 60-second session lease has one current boot/session per agent, not a global
Run slot. A new session increments the fence with checked overflow. A different
boot under a live lease commits quarantine; it does not take over. Expired leases
with missing/unsettled evidence become ActionRequired. Foundation settlement
means native dispatch is disabled: adding Runs/RunAttempts makes the guard deny
until B-06/B-08 provide authoritative admission checks. Lease expiry never releases
uncertain native capacity. Terminal revocation/quarantine still denies at numeric
exhaustion without wrapping; ordinary increments fail Capacity.

Connect replay returns the original accepted receipt and original expiry even
after renewal; latest identical Poll replay returns its original ACK without
extension. Older sequence is StaleReceipt. `ReadSession` is distinct: it returns
current database time, current fence/authorization, actual latest lease and an
explicit current-authority flag. It does not acquire or renew a lease. Lifecycle
Version changes on lifecycle transitions, not ordinary heartbeat; session authority
uses the locked fence and authorization epoch. No graceful-shutdown assumption.

The EF adapter stages replacement activation only after retiring the previous
active key/freeing live-grant uniqueness, with both saves inside one transaction.
A commit exception is CommitUncertain: there is no internal retry. The caller
reconciles the original request/claim/proof/session IDs against durable state.
Returned receipts never contain code hashes or certificate bodies.

Loads are bounded at 32 assignments, 64 credentials and 128 retained grants per
agent. Quota exhaustion fails closed rather than deleting authority. Cleanup
expires grants under the agent lock, retains terminal receipts at least 24 hours
and audit at least 90 days, deletes at most 100 rows/pass and does nothing when
recovery/native evidence is unsettled. No scheduler or credential/tombstone purge.
No assignment detach/delete API is introduced; revoked evidence conservatively
blocks provider deletion until a separately reviewed detach operation exists.

## Migration and acceptance gates

`20261111081939_AgentControlFoundation` is the only new proposed migration.
No application startup migration is added. Down first holds ACCESS EXCLUSIVE
over provider/secret and all five agent tables through guard and DDL. It rejects
non-revoked agents, recovery holds, unexpired leases, pending/active/retiring keys,
non-revoked assignments, live grants and grants inside the 24-hour receipt window.
It also rejects audit records inside their 90-day retention window.
This intentionally refuses destructive rollback while authority/evidence remains.
Any later Run schema needs its own rollback/integrity integration before dispatch.

1. Exact Up/Down, snapshot, SQL, FK/check/index definitions and independent
   security findings were reviewed before isolated application. Neither normal
   development application nor rollback is authorized by that approval.
2. Isolated PostgreSQL 18 test-only application/rollback was separately approved
   and executed. `FLOWPLANE_TEST_AGENT_MIGRATION_APPROVED=true` must be
   explicitly supplied in addition to prior migration gates. The shared test
   factory checks this **before** creating a database. A05/A09 wrappers require
   explicit approval too; never run them against development volumes. The approved
   Foundation wiring sets the gate only on its isolated PostgreSQL baseline step;
   do not report that job as verified from offline tests.
3. Executed fresh and Gate B upgrade through both EF and exact SQL, repeatability,
   twelve-table/five-history checks, valid/invalid ownership and lifecycle checks,
   active-key/live-grant/pin uniqueness, rollback guard and its admission race,
   safe quiescent rollback preserving all existing data and key fingerprints.
4. Executed real multi-context races: same/different actor grants, certificate
   substitution/global pin uniqueness, proof/confirm/rotation/revoke, exact expiry and
   overflow, original/latest ACK replay, lost commit ACK, conflicting incarnations,
   provider/config/direct-secret fencing and early deletion preserving discovery
   and owned secrets. Recovery used new scopes and existing process-recovery
   regressions; no EF InMemory substitute.
5. Normal development migration requires separate operational approval after all
   isolated acceptance. F4 transport, F5 polling/restart/TLS tests, F6 packaging and
   Windows-native runtime/service-token acceptance remain independently gated.

## Isolated acceptance evidence

Acceptance baseline: `main` at `caeb95c23076a970299a12a9f2f68b7366a6a579`.
The final complete persistence run executed **116 tests: 116 passed, zero failed,
zero skipped, exit 0**. This comprises 39 F3 PostgreSQL cases (the original ten
plus 29 transaction cases), six pure cleanup-identity guards and 71 existing
PostgreSQL regressions. Focused runs overlap this total and are not added again.
All VSTest runs used direct reviewed commands with one-time permission approval;
no global sandbox setting or reusable approval rule was changed.

| Required acceptance category | Actual evidence |
| --- | --- |
| Fresh bootstrap | EF applies all five migrations; the SQL path bootstraps the accepted four through EF, then applies the reviewed F3 SQL. Both reach twelve public tables. |
| Four-migration upgrade | Both EF and reviewed SQL preserve representative provider, discovery and existing data. |
| Repeat | A second EF update reports no pending migrations on both paths; non-idempotent raw CREATE TABLE SQL is not blindly repeated. |
| Exact schema | Actual columns/types/nullability/defaults, named checks, restrictive ownership FKs and valid unique/partial indexes match the reviewed SQL catalog. PostgreSQL 18 NOT NULL constraints are explicitly included. Invalid bounds/ownership, duplicate pins/active keys/live grants are rejected. |
| Enrollment | Concurrent same/different actors, key substitution, proof/confirmation races, expiry and lost acknowledgements preserve one activation and immutable receipts. |
| Credentials | Rotation replay and activation/revocation races cannot revive revoked or retiring authority; terminal denial remains durable at numeric exhaustion. |
| Provider context | Nine configuration/owned-credential/direct-secret versus Connect/Poll/Confirm races fence obsolete context atomically; populated inventory and ciphertext survive rejected deletion. |
| Sessions | Competing expired-lease acquisitions have one winner; old owners, conflicting incarnations, missing journal evidence and exhausted counters fail closed. |
| Replay/cleanup | Lost commit acknowledgements reconcile original identities without extending leases; reordered/changed poll sequences reject; bounded audit/grant cleanup preserves active and recovery evidence. |
| Rollback | Quiescent Down preserves prior tables/data/key-ring fingerprint; active, retained or unresolved authority rejects Down. Actual ACCESS EXCLUSIVE locks block concurrent enrollment until guarded DDL finishes. |

The reviewed SQL was unchanged during database acceptance. Staging's first
`git diff --cached --check` then detected a generated extra blank line at EOF in
each newly tracked SQL file. Only that final blank line was removed; comparison
proved every preceding byte and SQL statement unchanged. No migration/model or
database behavior changed, so the accepted database matrix was not repeated.
Final staged-artifact SHA-256:

- Upgrade: `af320528811276dbcee5a9f53b47f8f74c20cf48de838eb5faa53ba523788b07`.
- Rollback: `a419e0fda2bb398d2094c83d8c3bcde2bfb268134ffe74a7beadcaa246a8ee08`.

Two production defects were reproduced with failing real PostgreSQL tests, then
corrected minimally. Revocation previously rolled back when a live grant's
version was exhausted; terminal grant cancellation now saturates safely while
ordinary increments still fail closed. At exhausted epoch/fence values a recovery
hold could replay an old successful control receipt; Connect/Poll now reject the
hold before replay. ReadSession remains available with authority explicitly false.
Neither fix changes the migration, wire protocol or native-operation semantics.
Initial harness failures (catalog NOT NULL expectations, statement-time expiry
fixtures and obsolete regression assumptions) were corrected without relaxing
database constraints; failed TRX evidence was retained.

Across RED/GREEN and regression runs, 418 uniquely named disposable databases
were created and removed only after exact name/OID/owner/invocation-marker checks.
None remain uncertain. Private ignored evidence retains individual TRX results,
exact database identities, schema/history snapshots and before/after hashes;
credentials and protected key contents are not included in this document.

Development preservation comparison passed: four migrations, seven tables, one
provider, one encrypted secret, one Data Protection key, one generation, ten
observations and one discovery state. Complete row/schema/key-ring hashes,
container identities and health, persistent volume identity and root PFX
checksum/ownership/permissions/read-only mount were unchanged. All 18 pre-existing
B-02 files retain fingerprint
`33bff3e794957b28d93936f51782e42f2b4cfa8dfc7dafb034a5cde30b6804ab`.

Independent source/schema and final acceptance-evidence review found no unresolved
Critical, Important or Minor source finding. This is PostgreSQL acceptance, not
Kestrel trust, CNG, Windows service identity, native SSIS execution or F4 acceptance.
No real A05/A09 volume-managing wrapper was run during isolated acceptance or the
CI-wiring review. Fresh full-solution formatting after acceptance corrections was
verified manually in normal WSL: `dotnet format FlowPlane.slnx
--verify-no-changes --no-restore`, exit 0, no diagnostics. This is operator-executed
evidence, not a successful Codex formatter invocation. The Windows solution is
unchanged since its accepted full-format check. No application, test or schema
source changed in the CI-wiring follow-up.

## Foundation CI approval gate

Following separate isolated migration and CI-wiring approval, the existing
[Foundation workflow](../../.github/workflows/foundation.yml) supplies
`FLOWPLANE_TEST_AGENT_MIGRATION_APPROVED: "true"` only to the **Verify PostgreSQL
baseline** step of its PostgreSQL job. The approval is explicit in reviewed
workflow source, not a production runtime setting or a global environment value.
It runs on the existing pull requests targeting `main` and pushes to `main`;
there is no new dispatch, schedule, job, permission or secret. No GitHub workflow
is triggered by this uncommitted local change.

That step invokes the existing `tests/container/a05-postgresql-baseline.sh`.
On its fresh hosted CI runner it creates its own
`flowplane-a05-verification-<pid>` Compose project and project-scoped PostgreSQL
volume, checks actual EF migration/repeat behavior and five-history/twelve-table
results, and retains the existing readiness/restart checks and strict exit status.
It does not use the running development project, its volume, root PFX, credentials
or private configuration. Local invocations with the gate absent or not exactly
`true` still fail **before resource creation**. Do not run the volume-managing
wrapper against existing development resources.

The baseline step is **not** the 39-case F3 transactional PostgreSQL suite and
does not replace its separately accepted 116-test persistence evidence. The
ordinary backend job does not receive database configuration or this gate; gated
tests there may skip and are **not F3 database acceptance**. Never infer database
acceptance from an otherwise green unit-test job with skipped integration tests.

Local wiring verification parsed YAML with duplicate-key rejection, checked that
all existing jobs/triggers/permissions/commands were unchanged apart from the
step-local gate, and exercised the real shell gate against a Docker sentinel.
Before the change, two approval-wiring checks failed; after it, all four checks
passed. Missing/false approval made no Docker call; approved step configuration
reached only the isolated project's config command and stopped at the sentinel.
No real Docker or database operation ran. Hosted Foundation execution remains
pending a separately authorized commit/push; local checks do not claim it passed.
