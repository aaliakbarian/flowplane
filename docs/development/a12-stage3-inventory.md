# A-12 Stage 3: secure published inventory browsing

Backend-only increment of [issue #13](https://github.com/aaliakbarian/flowplane/issues/13).
No UI, native execution, registration, schema migration or SSISDB mutation is added.

## Contract

`GET /provider-instances/{id}/inventory` is exposed by the existing Nginx origin as
`GET /api/provider-instances/{id}/inventory`. It requires a valid JWT and the
existing exact issuer/subject trusted-operator policy for the requested provider.
Every response is `Cache-Control: no-store`.

Optional query parameters (single values only):

| Parameter | Meaning |
| --- | --- |
| `limit` | Default 50, minimum 1, maximum 100. Zero never means unlimited. |
| `schema` | Exact allowlisted SSIS observation schema ID below; otherwise all five. |
| `candidatesOnly` | `true` or `false` (default); compatible entry-point references only. |
| `cursor` | Opaque continuation from the preceding response, with unchanged filters/limit. |

Allowlisted schemas are `flowplane.ssis/discovery-folder.v1`,
`flowplane.ssis/discovery-project.v1`, `flowplane.ssis/discovery-package.v1`,
`flowplane.ssis/discovery-parameter.v1` and
`flowplane.ssis/discovery-environment-reference.v1`.

The 200 response contains `providerInstanceId`, `generationId`, `items` and
`nextCursor` (null when exhausted). Each item contains `schemaId`, exact
provider-owned `nativeIdentity`, typed allowlisted `metadata`, and nullable
`executableReference` (`providerInstanceId`, `providerType`, `schemaId`, `data`).
All Int64 SSIS identifiers/revisions in outward metadata are decimal strings;
format numbers and other Int32 fields remain JSON numbers. No password,
SecretReference, SQL connection string, parameter default, environment value or
execution parameter value is returned. Candidate eligibility does not establish
EXECUTE permission or guarantee successful package execution.

## Consistency and disclosure

1. Authorize the operator/resource and check current enabled provider, strict
   configuration, endpoint allowlist and Development-only certificate bypass.
2. Capture one complete Published generation, publication/provider/managed-secret
   versions and access context in a short read-only REPEATABLE READ transaction.
   As with the A-11b internal reader, a later failed/in-progress attempt invalidates
   serving context; there is no fallback to old observations.
3. Execute indexed SHA-256 native-key keyset queries with SQL `LIMIT limit + 1`.
   Exclude NotObserved; apply schema/candidate predicates before retrieval.
   Live-authorize the lookahead too before revealing that another page exists.
   Fetch at most three required parent identity records per selected/lookahead item from
   the same generation. Infrastructure does not interpret SSIS metadata.
4. End the PostgreSQL transaction. Resolve the credential for bounded use and
   recheck context before native IO. Check supported server/catalog access and
   public catalog surface. Query only selected native objects and required
   folder/project/package parents, using typed SQL parameters and `TOP (2)` to
   reject ambiguous matches. Read only approved metadata columns, never values.
5. Compare exact names, identities, active revisions and approved parameter and
   reference metadata with the published observations. Parent revision checks
   bracket the bounded reads. Native lookup follows SQL Server collation, but
   FlowPlane comparisons preserve exact ordinal identity, including Unicode/case.
6. Close native connections, reauthorize/recheck endpoint and current published
   generation/provider/credential context, then project the page. A mismatch or
   denied/unavailable native operation rejects the whole page, not selected items.

This is observational consistency, not an atomic cross-database snapshot. A native
change immediately after the final observation cannot be excluded without native
locking/transaction guarantees not claimed here. Binding and reference metadata
are checked even when the project LSN has not changed. No historical revisions
are read. PostgreSQL browsing does not paginate or truncate the A-11a native scan.

## Cursor lifecycle

Cursors are HMAC-SHA-256 authenticated, canonical base64url envelopes bound to
provider, generation, exact filters, page size, digest ordering/version and last
digest. Payloads contain no credentials or raw native names. They are opaque API
continuations, not authorization tokens or confidential storage.

The signing key is process-local cryptographic randomness, never persisted or
used for provider-secret protection. Cursors expire after 30 minutes and become
invalid after backend restart. Expiry is exclusive at the encoded Unix-second
boundary: a cursor is valid only while the current Unix timestamp is less than
its expiry. At or after expiry, or after backend restart, the client must restart
browsing at page one without reusing the rejected cursor. This preserves the
single-replica baseline without creating an optional root-key boot dependency or
performing key-ring operations. It does not implement shared-replica cursors.

## Safe failures

| Condition | HTTP |
| --- | --- |
| Missing/invalid authentication | 401 |
| Non-operator or explicit native permission denial | 403 |
| Missing provider | 404 |
| Invalid limit, filter, duplicate/unknown query field, or cursor | 400 |
| Superseded cursor generation | 409 `InventoryGenerationChanged` |
| No published inventory | 409 `InventoryNotPublished` |
| Changed context, visibility, identity, revision or metadata | 409; refresh/restart required |
| Native authentication/TLS/connectivity/timeout or storage unavailable | 503 |

Problem Details contains stable safe codes, not native SQL exception text.
Unknown-format definitions remain browsable observations, without executable
references. A complete empty published inventory returns 200 with an empty array,
after fresh native access checks; no published generation is a different state.

## Verification and boundaries

Tests use newly created isolated PostgreSQL 18 databases and disposable synthetic
credentials. Controlled real tests use the existing SSIS fixture read-only.
Coverage includes paging beyond 100 observations, repeatable-read consistency,
NotObserved exclusion, cursors, authorization, credential/configuration/publication
races, native denial/unavailability, metadata changes and Int64 precision.

The previously declined normal-backend key usability probe and sensitive-log scan
remain unexecuted. These feature tests do not replace them. Independent off-host
PFX/current-database backup remains pending; no persistent real credentials may
be created in the normal development database. Gate B and Accepted ADRs are
unchanged. Stage 4 UI and issue closure require separate authorization.
