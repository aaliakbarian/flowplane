# A-11b isolated PostgreSQL and SSISDB acceptance

Status: final implementation review pending. No commit, issue closure, A-12 work,
or migration outside disposable test databases is authorized by this record.

## Environment and safety

- Native WSL SDK 10.0.113 / runtime 10.0.13.
- Existing Compose PostgreSQL 18.6 service and named volume; no volume removal.
- SDK container `mcr.microsoft.com/dotnet/sdk:10.0.402-noble`, runtime 10.0.13.
- Only randomly named helper-owned `flowplane_a09_*` / `flowplane_a05_*`
  databases were migrated and dropped. No normal development database migration.
- The normal development schema hash matched before and after testing. All owned
  disposable databases were removed. No SQL Server fixture, permission or
  deployed package was modified.
- The ignored private SSIS environment supplied credentials without command-line
  values or diagnostic output. It is not part of the review diff.

Database-dependent tests require private `FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING`
configuration targeting the Compose service and explicit
`FLOWPLANE_TEST_DISCOVERY_MIGRATION_APPROVED=true`. The helper creates a new
database before applying migrations; the configured database is not a migration
target. Real fixture tests additionally require their existing private SSIS
configuration and `FLOWPLANE_TEST_SSIS_A11_ENABLED=true`. Ordinary Foundation CI
does not acquire a private SSIS dependency.

## Migration correction and acceptance

The real PostgreSQL RED test allowed a Published generation's ScanFinishedAtUtc
to become NULL. The same pending migration, EF mapping, designer and snapshot
were strengthened with `AND "ScanFinishedAtUtc" IS NOT NULL`. Its exact generated
SQL diff was displayed before applying the corrected version to fresh disposable
databases. The existing migration ID is unchanged; no fourth migration was added.

Verified against actual PostgreSQL, not EF InMemory:

- Fresh bootstrap through three migrations and exactly seven public tables.
- Upgrade from A-09 with existing ProviderInstance and encrypted-secret data;
  ciphertext/reference preservation and successful secret resolution.
- Repeat application, rollback of only the three inventory tables and re-upgrade.
- Equality of actual column/type/nullability/collation, constraint and index
  catalog definitions after EF application versus generated upgrade SQL.
- Six foreign keys, four composite ownership foreign keys, five RESTRICT delete
  actions, twelve indexes including primary/alternate keys, and reviewed partial
  active-claim/candidate predicates.
- Rejection of cross-instance pointers/observations, invalid payload pairs,
  missing metadata, malformed digest length, negative counts, missing publication
  timestamp, duplicate active claims and deletion that bypasses retirement.

## Publication, consistency and recovery

Complete Succeeded, complete compatible Partial with unknown-format observations,
and empty scans publish. Incomplete/Failed/Unavailable results, incorrect durable
counts and native-key collisions do not replace the previous complete inventory.

Tests verify independent row/byte flush bounds, counts/change detection, duplicate
identity rejection, SHA-256 native key indexing with exact ordinal equality,
minimal payload-free NotObserved markers and their omission on a further missing
refresh. Actual JSONB round trips preserve references, Unicode/case distinctions,
canonical metadata and fingerprints, including 64-bit integer metadata; repeat
refreshes do not manufacture changes. This is evidence for the approved document
shapes, not a universal JSON numeric canonicalization protocol.

Real concurrent PostgreSQL connections exercise competing claims, expiry fencing,
provider configuration changes, and direct/provider-owned managed-secret replacement
while publication waits on locks. Readers execute multiple queries against one
read-only REPEATABLE READ snapshot across publication and cleanup.

Recovery coverage includes bounded cleanup interrupted between invocations,
durable retirement interrupted before purge, a genuinely killed staging subprocess
after 128 durable observations, restart/expiry recovery, and injected failures
immediately before and after actual transaction commit. Uncertain acknowledgement
is reconciled by durable refresh ID, never inferred to be rollback.

Independent review found that ordinary cancellation discarded the coordinator's
refresh ID. RED reproduced it. The bounded fix returns cancellation through
`ProviderDiscoveryRefreshCanceledException`, preserving the original token and
optional RefreshId. Actual commit-boundary tests recover that ID and reconcile
Published versus Uncertain without aborting a possibly committed publication.

Real integration also exposed ProviderType object-identity comparisons after EF
rehydration. Four focused RED tests covered coordinator selection, discovery,
validation and native-reference decoding. Those boundaries now use existing value
equality; Domain and server/version/TLS policies were not changed.

## Final results

| Verification | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Full native Release solution | 295 | 0 | 0 |
| API, including isolated migration/readiness recovery | 26 | 0 | 0 |
| PostgreSQL persistence, encryption, publication and recovery | 37 | 0 | 0 |
| Architecture | 9 | 0 | 0 |
| Provider/application/configuration unit tests | 57 | 0 | 0 |
| SSIS provider unit tests | 147 | 0 | 0 |
| Real SSISDB project, native WSL | 19 | 0 | 0 |
| Real SSISDB project, non-root SDK container | 19 | 0 | 0 |

Each real project run includes six A-10 validation, six A-11a discovery, six A-11b
publication cases and one scan smoke test. The controlled native environment
remains SQL Server 2025 RTM 17.0.1000.7 and the unchanged FlowPlaneA10 /
FlowPlaneA11Fixture. No new SQL support or execution certification is implied.

The final container used non-root UID/GID 1000:1000, source and package cache
mounted read-only, and internal temporary test output copied into the owner-only
host review directory. An attempted arbitrary-UID 1001 SDK invocation failed at
NuGet mutex initialization before tests; host file permissions were not widened.
The reference non-root run passed all real tests. No new SDK image/user provisioning
architecture is introduced.

Release warnings-as-errors build, formatting, online transitive NuGet audit,
Foundation documentation contract, shell syntax, Compose validation and diff
checks passed. Raw inventory rows, outward results, captured diagnostics and real
fixture metadata were checked for credential/locator/connection-string/private-key
and sensitive-value leakage without printing secret values.

The A-05/A-09 volume-deleting shell wrappers were intentionally not executed under
this approval. Their migration/readiness and encryption/lifecycle contracts ran
against the existing service through isolated-database tests. Readiness recovery
used ALLOW_CONNECTIONS on one owned database, not stopping the shared service.

## Review limits

No Critical finding remains. The Important receipt-access finding was fixed and
verified. Minor, deferred: the cleanup receipt anti-join could include the known
ProviderInstanceId to align with the observation index; no measured regression
or correctness failure was established, and no performance rewrite was made.

Future readers still require independent FlowPlane authorization and fresh native
visibility; a receipt ID is not authorization. The short reader snapshot does not
continuously revalidate external grants. A-11a's SSISDB consistency checks do not
prove a transactional snapshot or eliminate change-and-reversion races. These
tests do not certify every process-death instruction boundary or production-scale
catalog latency. No current GitHub-hosted run is claimed for uncommitted changes.
