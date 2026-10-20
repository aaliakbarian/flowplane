# A-11b current-inventory migration review

Status: isolated PostgreSQL 18 tests exercised; **final implementation review pending**.
The normal development database has not been migrated. See the
[post-migration acceptance record](a11b-current-inventory-acceptance.md).
GitHub [#15](https://github.com/aaliakbarian/flowplane/issues/15) and parent
[#12](https://github.com/aaliakbarian/flowplane/issues/12) remain open.

The [A-11 plan](a11-discovery-implementation-plan.md) and the approved final design
govern this change. SSISDB remains authoritative. This inventory is not Pipeline
registration, a historical SSIS version store, or a cached-browsing API.

## Migration artifacts and ordering

The generated [CurrentDiscoveryInventory migration](../../../src/backend/FlowPlane.Infrastructure/Persistence/Migrations/20261020082907_CurrentDiscoveryInventory.cs)
contains the exact `Up` and `Down` operations. Its
[designer](../../../src/backend/FlowPlane.Infrastructure/Persistence/Migrations/20261020082907_CurrentDiscoveryInventory.Designer.cs)
and [model snapshot](../../../src/backend/FlowPlane.Infrastructure/Persistence/Migrations/FlowPlaneDbContextModelSnapshot.cs)
contain the complete EF model. Generated SQL and EF application were compared in
disposable PostgreSQL 18 databases after conditional approval. Application outside
isolated test databases still requires separate approval.

Ordering is `InitialPostgreSqlBaseline` → `ProviderConfigurationAndSecretStorage`
→ `CurrentDiscoveryInventory`. Isolated fresh-bootstrap, A-09 upgrade, repeated
application and rollback/re-upgrade tests passed, with three migration-history
rows and exactly seven public tables after upgrade. Existing ProviderInstances
and encrypted secrets survived upgrade and rollback.
There is no startup migration, data backfill, existing-table alteration, or change
to the A-09 secret/key-ring schema.

`Up` creates generations, observations and states, in that order. `Down` drops
observations, states and generations. Rollback destroys discovery state and
inventory; it does not drop ProviderInstances, managed secrets or key-ring data.

## Schema

All identifiers are `uuid`; counters, versions and fences are `bigint`; timestamps
are `timestamp with time zone`; the default nullability below is **NOT NULL**.
The generated migration/SQL is the authoritative exhaustive column definition.

| Table | Columns and nullable exceptions |
| --- | --- |
| ProviderDiscoveryStates | ProviderInstanceId; nullable CurrentGenerationId, InFlightGenerationId, LastAttemptGenerationId; PublicationVersion, FenceToken; Lifecycle `varchar(16)` |
| ProviderDiscoveryGenerations | RefreshId, ProviderInstanceId, nullable BaseGenerationId; FenceToken, ExpectedPublicationVersion, ProviderVersion; ProviderType `varchar(64)`, CredentialKind `varchar(16)`, nullable ManagedSecretVersion; AccessContextId; StartedAtUtc, ExpiresAtUtc; nullable ScanStartedAtUtc, ScanFinishedAtUtc, FinishedAtUtc, PublishedAtUtc; Status `varchar(16)`, Version, ScanCompleted `boolean`; nullable ScanOutcome and SourceCompatibility `varchar(16)`; ObservationCount, CandidateCount, IncompatibleCount, AddedCount, ChangedCount, UnchangedCount, NotObservedCount; nullable Error `varchar(32)` |
| ProviderDiscoveryObservations | ProviderInstanceId, RefreshId, NativeKeyDigest `bytea`, NativeKey `text COLLATE "C"`, SchemaId `varchar(256)`, Visibility `varchar(16)`; nullable MetadataJson `jsonb`, CandidateSchemaId `varchar(256)`, CandidateJson `jsonb`, ContentFingerprint `bytea`; LastObservedAtUtc, ObservedAccessContextId, ObservedProviderVersion, nullable ObservedSecretVersion and NotObservedAtUtc |

### Keys, foreign keys and deletion

- State PK: ProviderInstanceId. Generation PK: RefreshId; alternate unique key:
  (ProviderInstanceId, RefreshId). Observation PK:
  (ProviderInstanceId, RefreshId, NativeKeyDigest).
- All three state-generation pointers and observation ownership use composite
  (ProviderInstanceId, generation ID) foreign keys to the generation alternate
  key. A state or observation cannot point into another ProviderInstance.
- Generation → ProviderInstance, observation → generation and state → generation
  are `RESTRICT`. State → ProviderInstance is `CASCADE` for the small state row
  only. There is no catalog-sized observation cascade.
- BaseGenerationId is provenance, not a foreign key retaining old payloads.
- Deletion first locks the ProviderInstance and owned secret, marks its state
  Deleting, clears pointers and fences scanners. Payload/header removal uses
  short batches; final ProviderInstance/owned-secret removal is atomic. A crash
  after retirement is resumable. Direct deletion that bypasses this protocol is
  intentionally restricted by existing generation foreign keys.

### Indexes and checks

Besides the primary/alternate keys, the migration creates:

- Generation (ProviderInstanceId, FinishedAtUtc) and (Status, ExpiresAtUtc) indexes.
- Unique `UX_DiscoveryGeneration_ActiveClaim` on ProviderInstanceId, with exact
  predicate `"Status" IN ('Staging', 'Preparing', 'Ready')`.
- Observation (ProviderInstanceId, RefreshId, Visibility) index and candidate
  index (ProviderInstanceId, RefreshId, CandidateSchemaId), with exact predicate
  `"Visibility" = 'Observed' AND "CandidateSchemaId" IS NOT NULL`.
- One composite index for each state-generation pointer. There is no JSONB GIN
  index or unbounded native-name B-tree key.

Checks enforce lifecycle/status allowlists, positive versions/fences, nonnegative
counts, candidate/incompatible counts not exceeding observations, forward expiry,
managed/environment credential-version consistency, and different current/inflight
pointers. Published rows require non-null ScanFinishedAtUtc, complete coverage,
compatible source and Succeeded
with zero incompatible definitions or Partial with positive incompatible count.
Observation checks require 32-byte digests, nonempty identity/schema and coherent
Observed versus payload-free NotObserved shapes. Candidate schema/document must
both be present or both absent. Cross-row counts, source versions, actual metadata
schema validation and pointer phase validity are additionally enforced by the
publication protocol; SQL constraints alone are not that protocol.

Native-key SHA-256 is an index key, **not identity equality**. Every duplicate
digest is compared against the exact ordinal native key, including against the
previous generation. Different keys with the same digest fail safely as
NativeKeyCollision; identical keys delivered twice fail as DuplicateNativeKey.
Neither case silently overwrites a row.

## Streaming, publication and recovery

The A-11a scanner and sink contracts are unchanged. Each awaited append provides
backpressure into at most 128 records / 256 KiB of pending writes by default,
with one oversized observation handled alone. There is no total catalog limit.
Rows are provider-schema-validated before reaching persistence. Infrastructure
does not interpret SSIS fields or introduce a universal object hierarchy.

Metadata/reference documents are stored as JSONB. Rehydration uses their canonical
document representation; fingerprints are length-framed SHA-256 over the canonical
schema/document/reference tuple, not PostgreSQL JSONB text formatting. PostgreSQL
round-trip identity/fingerprint tests pass for the approved integer/string/Unicode
document shapes, including exact case-sensitive and Unicode native identities.
An individually oversized document or storage error fails the refresh, never
silently truncates it. Native metadata remains per-row, not one project/catalog blob.

| State / terminal evidence | Action |
| --- | --- |
| Staging | Incremental durable writes; never reader-visible |
| Preparing | Validate terminal summary, count durable rows, compare fingerprints and add minimal missing-identity markers in batches |
| Ready | Complete compatible source; exact durable counts checked; eligible for final fenced transaction only |
| Succeeded + complete + compatible + no incompatible definitions | Publish, including an empty inventory |
| Partial + complete + compatible + incompatible definitions | Publish all observations; incompatible definitions remain ineligible |
| Incomplete, incompatible/unavailable source, count mismatch or failed consistency checks | Do not publish |
| Failed, Cancelled, Superseded or Abandoned | Retain prior current pointer; staged payload is eligible for bounded cleanup |
| Published | Immutable complete generation; atomically advance pointer/publication version |
| Commit acknowledgement uncertain | Reconcile by durable refresh ID; never infer rollback from an exception or overwrite an uncertain commit with Failed |

Transactions are short. The external scan holds no PostgreSQL transaction.
Lock order is ProviderInstance → managed secret → discovery state → generation
when those resources are involved; row-write/cleanup paths need only the latter
subset. Publication verifies ProviderInstance version/enabled state, managed-secret
version, fence, expected publication version, base generation and PostgreSQL expiry.
Direct managed-secret replacement is also detected even without ProviderInstance
version change. The A-09 provider-owned credential replacement now takes the
ProviderInstance lock before touching its secret.

The operation allowance is `max(3600, claim + configured SSIS scan + preparation +
publication)` seconds, with checked arithmetic. Defaults are 30 + 1800 + 300 + 30;
the API derives scan time from the same `FlowPlane:Ssis` options as the scanner.
Durable expiry uses PostgreSQL time; local linked cancellation tokens bound phases
and elapsed operation time. Competing live claims fail; expired claims are fenced
and abandoned before replacement. Caller cancellation after possible commit still
propagates as ProviderDiscoveryRefreshCanceledException once a refresh is claimed,
with the original cancellation token and optional durable RefreshId available for
reconciliation, without an abort that could overwrite successful publication.

`FlowPlane:DiscoveryInventory` controls ClaimTimeoutSeconds (1–120),
PreparationTimeoutSeconds (1–7200), PublicationTimeoutSeconds (1–120),
ReaderTimeoutSeconds (1–120), WriteBatchSize (1–1000), WriteBufferBytes
(1–4194304) and CleanupBatchSize (1–10000). Standard environment overrides use
`FlowPlane__DiscoveryInventory__<setting>`. ScanTimeoutSeconds is not independently
selected by the API: it uses `FlowPlane__Ssis__OverallTimeoutSeconds` (1–86400).
These are operator settings, not ProviderInstance configuration.

## Visibility, retention and readers

A missing previous Observed identity becomes NotObserved for one successful
refresh only: identity, schema, last-seen/access provenance and missing timestamp;
**no metadata, fingerprint or executable reference**. If still missing on the next
successful refresh, omit it. Missing visibility never means confirmed deletion.

Failed refreshes retain the previous complete pointer for integrity, **not cached
disclosure authorization**. The internal reader requires the currently verified
refresh ID, unchanged provider/managed-secret versions, no pending refresh and no
failed newer attempt. Future callers must independently establish authorization
and fresh native visibility. An environment-variable locator or unchanged login
does not prove unchanged external permissions; no cached fallback is permitted.

Each reader captures one generation inside a short read-only REPEATABLE READ
transaction (30-second default). Every subsequent query stays in that snapshot,
including while another connection publishes or cleans up. Consume sequentially,
dispose promptly and do not await external calls inside the reader. This is not an
SSISDB transactional snapshot or an A-12 HTTP endpoint.

Cleanup runs explicitly/on refresh claim, without a scheduler: default 1000 rows
per transaction, at most ten batches per invocation. Current/inflight payloads and
pointer targets are protected. Superseded/abandoned payloads are removed; minimal
unreferenced terminal receipts are eligible after seven days, not native history.
Latest-attempt/current receipts stay referenced. Beyond receipt retention,
reconciliation may correctly return Uncertain rather than inventing a result.

## Verification gate

Database tests require `FLOWPLANE_TEST_DISCOVERY_MIGRATION_APPROVED=true` before
creating or migrating a database. This guard was enabled after approval only for
helper-owned disposable databases on Compose PostgreSQL 18. A skip is never
acceptance evidence. See the [acceptance record](a11b-current-inventory-acceptance.md)
for schema, concurrency, JSONB, process-kill, reconciliation and real SSISDB results.

Read-only review found no Critical issue. Its Important cancellation receipt-access
finding was fixed with RED/GREEN and actual commit-boundary tests. An unmeasured
cleanup-query indexing improvement remains deferred. No exhaustive crash-point,
production-scale throughput or universal JSON numeric canonicalization claim is made.

No accepted ADR, external SSISDB fixture/grant, A-12 API or ordinary Foundation CI
dependency on private SSISDB is changed. Commit, push, issue closure and migrations
outside disposable databases remain unauthorized.
