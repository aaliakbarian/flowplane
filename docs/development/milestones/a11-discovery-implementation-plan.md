# A-11 Complete Discovery and Current Inventory Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` for native execution after explicit implementation approval. Follow the RED/GREEN and review gates below; this plan does not authorize code changes, external fixture operations or commits.

**Goal:** Scan all currently visible SSISDB projects and approved current metadata, then atomically publish a credential-scoped current inventory in FlowPlane PostgreSQL.

**Architecture:** A-11a streams provider-owned observations through a narrow Application sink and returns a terminal scan summary. A-11b supplies durable staging, comparison and atomic publication while retaining the previous complete inventory after failure. SSISDB remains authoritative and no discovery operation creates a Pipeline.

**Tech stack:** Existing .NET 10, Microsoft.Data.SqlClient 7.1.1 in the SSIS provider, EF Core/Npgsql/PostgreSQL 18 in Infrastructure, xUnit v3/VSTest and the patched non-root SDK-container test path. No new infrastructure or package changes are expected.

**Spec:** Approved complete-scan/current-inventory refinement in [SSIS Provider Technical Design, section 7](../../architecture/providers/ssis-provider-technical-design.md#7-discovery-design), under the [Milestone A plan](milestone-a-foundation-and-ssis-discovery.md#a-11--read-only-ssis-discovery). The technical design records product semantics; this file specifies the implementation tasks and review gates.

## Global constraints

- Parent A-11 remains [issue #12](https://github.com/aaliakbarian/flowplane/issues/12) in Milestone A; [A-11b / #15](https://github.com/aaliakbarian/flowplane/issues/15) is blocked by [A-11a / #14](https://github.com/aaliakbarian/flowplane/issues/14). GitHub is the live tracker.
- Status of this refined plan: Planned; implementation refactoring is not authorized by the documentation approval.
- Work in the existing authoritative main checkout. Preserve every existing modified/untracked implementation, test, configuration and fixture file. No reset, clean, stash, discard, stage or commit during planning.
- Every invocation covers all currently visible projects. No SSISDB result pagination, total catalog-size ceiling, candidate cap or parameter/environment-reference truncation.
- Unknown-format projects/packages remain approved observations with explicit candidate ineligibility. Non-entry-point packages remain project contents, not executable candidates.
- Preserve SQL major 17 Supported only within the evidenced A-10 boundary; 15/16 remain Unverified. Preserve independent project format 1 and package format 8 recognition. Discovery needs its own evidence.
- Domain/Application remain SSIS/SqlClient-free; Infrastructure remains free of a Providers.Ssis dependency. The envelope is deliberately narrow: no universal object hierarchy, Node/Edge model or speculative plugin SDK.
- Never retrieve/persist parameter defaults, environment values, execution parameter values, credentials, connection strings or private keys as discovery observations. Use explicit public-view column allowlists.
- Capture the current active project LSN and current package revisions. No object_versions scan, historical browsing/restoration/comparison, old ISPAC retrieval or FlowPlane SSIS-version history.
- Refresh current bindings/references even when LSN is unchanged. Consistency checks do not establish an SSISDB snapshot or authorize isolation configuration changes.
- Preserve previous complete inventory after incomplete scans; failed refreshes/revoked access must not authorize cached disclosure. Missing visibility is not deletion.
- No A-12 API/UI, Pipeline/Run/RunAttempt, execution/cancellation/monitoring, artifact parsing, graph work, scheduler/queue or application deployment.
- External fixture build/deployment/configuration are separately reviewed operations. Do not run configure-catalog.sql or modify external SSISDB under the current planning authorization.

## Review focus

1. One project is larger than a batch's typical metadata: stream its rows without a whole-project array or silent truncation; A-11a.2 tests a slow sink and large generated project.
2. Unknown project/package formats precede valid packages: retain every observation and only mark candidates ineligible; A-11a.1/.2 tests formats independently.
3. Bindings change without an LSN change, or a project disappears mid-scan: invalidate observed inconsistent refreshes without retry loops; A-11a.3 and A-11b.2 test both.
4. Credentials/grants change while old observations exist: fence obsolete publication and prohibit cached fallback on failed authorization/visibility; A-11b.2 tests this explicitly.
5. Cancellation/crash/database failure occurs immediately before or after publication: readers see the prior or new complete generation, never staged rows; A-11b.2/.3 test commit boundaries and recovery.

## Existing work: retain versus replace

The planning baseline is main at `78147b040f7dbeffe27011c0630431d50aa41d36`, with 15 modified and 24 new A-11 files. These are uncommitted working files, not completed acceptance of the revised scope.

| Existing component | A-11a disposition | A-11b impact |
| --- | --- | --- |
| Domain NativeDefinitionReference and Application native-reference codec | Retain exact provider-qualified executable identity and ordinal canonical equality | Persist references only for eligible executable candidates |
| SsisNativeDefinitionReferenceCodec and its identity tests | Retain Unicode/case/path and redeployment identity coverage | Reuse for stored eligible candidate references |
| ProviderDiscoveryMetadataDocument and provider-owned codecs | Retain controlled versioned-document mechanism; split candidate-wide arrays into scoped observation documents | Persist validated documents without interpreting SSIS fields |
| IProviderDiscoveryCapability / ProviderDiscoveryResult | Replace whole-list/truncation signature with sink plus terminal summary; retain narrow capability boundary | Application refresh coordinator consumes that interface |
| ISsisDiscoveryQuery / SsisSqlDiscoveryQuery | Replace candidate scope/rank limits with project manifest and parameterized batches; retain public-column allowlists and sanitized failure mapping | Sink persists rows incrementally |
| SsisDiscoveryMapper / SsisDiscoveryData | Replace catalog-sized lists, repeated metadata association and truncation branches with streaming observation mapping | Persist project-scoped metadata once |
| SsisDiscoveryOptions and config/DI tests | Replace total limits with project batch size; preserve standard ASP.NET configuration and fail-fast numeric validation | Reuse options; never persist them in ProviderInstance JSONB |
| SsisProviderDiscoveryCapability | Retain secret/TLS/version/error checks; refactor scan orchestration and lifetime | Coordinator owns staging/publication, never provider-owned PostgreSQL writes |
| A-10 connection-settings/classifier/compatibility helpers | Retain reviewed shared behavior and A-11-safe additions; do not broaden support claims | Regression only |
| Native identity, eligibility, redaction and provider failure tests | Retain assertions; adapt to streaming observations | Extend to persistence/disclosure tests |
| Truncation and bounded-list contract tests | Replace with complete coverage, bounded-buffer/backpressure and terminal-completion tests | Add publication tests rather than weakening assertions |
| API composition/config and Compose/example/docs | Retain composition and environment conventions; update renamed options after implementation approval | Add only required registration for inventory services |
| FlowPlaneA11Fixture sources, README and reviewed setup/verification SQL | Retain project deployment model, entry/non-entry packages, parameters and relative reference | Use after separate external setup approval; no fixture mutation now |
| RealSsisDiscoveryTests and ignored private configuration | Preserve six permission/auth/TLS scenarios; adapt result/metadata assertions | Extend to published inventory without losing A-10's separate six cases |

No existing file is discarded by this reconciliation. Old integration/contributor configuration text describes the current unrefactored implementation; update it alongside A-11a's actual configuration change, not ahead of executable behavior.

## Planned contract handoff

These signatures are implementation-plan targets, not an approved generic provider SDK:

- `IProviderDiscoveryCapability.ScanAsync(ProviderInstance instance, IProviderDiscoverySink sink, CancellationToken cancellationToken) -> Task<ProviderDiscoveryScanResult>`.
- `IProviderDiscoverySink.AppendAsync(ProviderDiscoveryObservation observation, CancellationToken cancellationToken) -> ValueTask`; the provider awaits each write and does not activate inventory.
- `ProviderDiscoveryObservation`: ProviderInstance ID, opaque provider-owned native observation key, typed/versioned metadata document and optional executable NativeDefinitionReference. Scope/associations remain in the SSIS document, not a universal parent/child hierarchy.
- `ProviderDiscoveryScanResult`: outcome, explicit ScanCompleted, checked 64-bit counts and aggregated safe issue codes/counts. No catalog array, continuation token or IsTruncated. Partial compatibility is permitted only after complete scanning; incomplete coverage cannot publish.
- A-11b introduces `ProviderDiscoveryRefreshService.RefreshAsync(Guid providerInstanceId, CancellationToken cancellationToken) -> Task<ProviderDiscoveryRefreshResult>`, plus `IProviderDiscoveryInventoryStore` operations BeginRefreshAsync, AppendAsync, PublishAsync and AbortAsync. PublishAsync checks expected ProviderInstance and publication versions in the same short transaction as the pointer update. Final storage signatures are reviewed at the migration gate after A-11a's accepted handoff.

## A-11a — Complete read-only SSIS scan

**Status:** Planned. **Depends on:** A-10 evidence and explicit A-11a implementation approval. **Produces:** reviewed streaming observation/sink contract, complete scan and real discovery evidence. **No inventory migration in this child.**

### A-11a.1: Observation contract, identity and options

**Files:** Modify `src/backend/FlowPlane.Application/Providers/IProviderDiscoveryCapability.cs`; create `ProviderDiscoveryObservation.cs` beside it. Refine `src/backend/FlowPlane.Providers.Ssis/Discovery/SsisDiscoveryMetadataCodec.cs` and `SsisDiscoveryOptions.cs`. Test in `tests/unit/FlowPlane.ProviderConfiguration.Tests/ProviderCapabilityContractTests.cs`, `tests/unit/FlowPlane.Providers.Ssis.Tests/SsisDiscoveryOptionsTests.cs` and `SsisNativeDefinitionReferenceCodecTests.cs`.

- [ ] RED: Add `Complete_scan_summary_separates_coverage_from_compatibility` and `Unknown_formats_remain_observations_without_candidate_references`; assert no catalog-sized result collection, truncation flags or universal hierarchy fields. Pin exact native identities and independent format eligibility.
- [ ] Run the affected projects with `dotnet test <project> --nologo -m:1`; record meaningful missing-contract/behavior failures before changing implementation.
- [ ] GREEN: Introduce only the handoff above and provider-owned row metadata schemas. Preserve native-reference v1; revise the uncommitted candidate metadata schema consistently with every consumer.
- [ ] Bind MaxProjectsPerBatch (tentative 20; range 1-100), connection timeout (5; 1-60), command timeout (15; 1-600), overall timeout (1800; 1-86400). Overall must cover individual timeouts. Reject invalid text/numbers, zero, overflow and retired total-limit keys explicitly. Update Program.cs, provider DI, Compose, example and configuration docs in this task.
- [ ] GREEN tests: default/custom/environment configuration, boundaries/retired keys, no per-ProviderInstance settings, and host creation without SSIS availability. Review numeric settings before finalizing them as implementation defaults.

### A-11a.2: Complete project-batch retrieval and streaming mapping

**Files:** Refactor `Discovery/ISsisDiscoveryQuery.cs`, `SsisSqlDiscoveryQuery.cs`, `SsisDiscoveryMapper.cs`, `SsisProviderDiscoveryCapability.cs`; create provider-internal `SsisProjectManifest.cs` for a disposable sequential manifest. Tests: `SsisSqlDiscoveryQueryTests.cs`, `SsisDiscoveryMapperTests.cs`, `SsisProviderDiscoveryCapabilityTests.cs`.

- [ ] RED: Generate more than 100 eligible candidates, 200 parameters and 50 references; assert none are lost. Include unknown formats and non-entry points, an empty folder/project, several batches and one large project. Assert batch sizes 1 and 20 produce equal complete observations.
- [ ] RED: A slow sink blocks upstream progress; a failing sink stops the scan; instrumentation proves bounded buffered records without relying on flaky GC/RSS thresholds.
- [ ] GREEN: Read all visible folders/projects into a restricted, disposable sequential manifest, then pass at most MaxProjectsPerBatch project IDs as SQL parameters. The temporary manifest stores approved metadata only, uses restrictive permissions and is removed on success/failure; it provides no external pagination or inventory publication.
- [ ] Stream explicit public-view rows and map observation keys/associations without catalog arrays, per-package duplication of project metadata or a whole-project JSON blob. Keep SQL ordering deterministic and native name matching faithful to SQL collation; do not use int ROW_NUMBER conversion or unsafe limit-plus-one arithmetic.
- [ ] Query all approved package/parameter/reference rows for each project; candidate eligibility is a derived classification, not a filter that removes observations. Use checked 64-bit counters and bounded aggregate diagnostics instead of an issue object per catalog row.
- [ ] Verify SQL never selects design_default_value, default_value, environment_variables.value, execution values or object_versions; no artifact requests or SSISDB writes. Run focused query/mapping/security tests to GREEN.

### A-11a.3: Completion, consistency and controlled real acceptance

**Files:** Provider scan/query files above; existing capability tests, `tests/integration/FlowPlane.Ssis.Integration.Tests/RealSsisDiscoveryTests.cs`, `docs/development/ssis-integration-testing.md`, fixture README/verification SQL only if reviewed assertions require adjustment.

- [ ] RED: Inject LSN change, unchanged-LSN binding/reference change, project disappearance, final manifest membership change, provider timeout and caller cancellation. Assert no complete terminal result, no retry loop and no sensitive error text.
- [ ] GREEN: Bracket project reads with active revision checks, verify deterministic metadata fingerprints and compare the final visible manifest. Report detected instability safely. Record the observation interval; explicitly retain the limitation that no SSISDB snapshot or elimination of change-and-reversion races is proven.
- [ ] Preserve A-10 SQL/TLS/secret behavior. Use an operation-owned timeout independent of future HTTP hosting; caller cancellation propagates, while provider timeout returns Unavailable. No fire-and-forget worker or queue.
- [ ] After separate fixture authorization/setup, run all six A-11 cases in native WSL and the patched non-root SDK container, with MaxProjectsPerBatch=1 crossing the A-10/A-11 projects. Assert the fixture's non-entry point remains observed but not a candidate; all approved parameter/reference associations and redaction remain correct. Require zero failures and zero skips. Without deployment, record acceptance as Blocked.
- [ ] Run separate six-case A-10 real regressions in both paths, unit/architecture/Release tests, NuGet audit and Foundation contracts. Present evidence for A-11a review; do not begin A-11b or commit automatically.

## A-11b — Current discovery inventory publication

**Status:** Planned; blocked by A-11a. **Consumes:** accepted ScanAsync/sink/summary contract. **Produces:** durable complete inventory and refresh/failure/access semantics. Explicit implementation and schema approval are required.

### A-11b.1: PostgreSQL staging and schema

**Files:** Create Application `Providers/IProviderDiscoveryInventoryStore.cs` and `ProviderDiscoveryRefreshService.cs`; Infrastructure `Persistence/Discovery/ProviderDiscoveryInventoryStore.cs` and narrowly scoped persistence records. Modify `FlowPlaneDbContext.cs` and Infrastructure DI. Add one migration and updated model snapshot. Add `tests/integration/FlowPlane.Persistence.Tests/ProviderDiscoveryInventoryTests.cs`.

- [ ] RED: Controlled PostgreSQL tests fail for missing staging/current inventory, typed observation round-trip, complete empty inventory and unknown-format observation retention.
- [ ] Review migration/schema before applying: state row holds ProviderInstance ID/active refresh/publication version; refresh row holds source configuration/access context, timestamps, coverage/compatibility/status and safe counters; generation item holds opaque native key, schema-approved metadata, optional candidate reference, timestamps and visibility classification. Use foreign keys and generation/key uniqueness; identity equality must not depend on PostgreSQL locale case folding.
- [ ] GREEN: Implement row-wise/batched staging without tracking the whole catalog in EF. Infrastructure does not decode SSIS vocabulary. Compare canonical fingerprints to previous observations but always perform the external refresh. Store current/last-known observations only, without accumulating native revision history.
- [ ] Verify the migration adds only discovery inventory structures/indexes. No Pipeline/Run/Schedule/history/artifact/value storage, no change to secret/key-ring schema. Apply/bootstrap twice through the local EF tool and verify migration history/model consistency.

### A-11b.2: Atomic publication, concurrency and disclosure controls

**Files:** Application refresh coordinator/store contract and Infrastructure store above; new `tests/unit/FlowPlane.ProviderConfiguration.Tests/ProviderDiscoveryRefreshTests.cs` and PostgreSQL inventory tests.

- [ ] RED: Inject failure after staged rows, cancellation before publication, database failure during publication, competing completed refreshes, and ProviderInstance/credential changes. Assert the prior complete active generation and inaccessible staging remain intact.
- [ ] GREEN: Publish in a short PostgreSQL transaction only if ScanCompleted and consistency checks passed and expected configuration/publication versions still match. Atomically finalize status and change the active pointer; roll back on conflict. Distinguish compatibility Partial from incomplete coverage. Handle a committed-but-unacknowledged publication through its durable refresh ID rather than overwriting blindly.
- [ ] RED/GREEN: A project missing from a complete visibility-filtered scan becomes NotObserved, never Deleted. Retained last-known payloads are explicitly stale, outside current candidates and bound to their original access context.
- [ ] RED/GREEN: Invalidate serving context on managed credential/configuration changes. Test external env credential/grant changes even when ProviderInstance version is unchanged. No authorization or live-visibility failure may fall back to old payloads. A-11 adds no caller-facing cached-read API; A-12 must authorize ProviderInstance access and validate fresh native visibility before exposing inventory. Authentication alone and cached SQL principal identity do not authorize disclosure.
- [ ] RED/GREEN: Unchanged LSN with changed approved bindings/references updates observations. Identical refreshes remain correct without skipping reads. Canary SQL secrets and parameter/environment values are absent from raw PostgreSQL, JSON, logs, exceptions and outward summaries.

### A-11b.3: Recovery and end-to-end acceptance

**Files:** Inventory coordinator/store/tests above; extend `RealSsisDiscoveryTests.cs` for publication and add an isolated `tests/container/a11-discovery-inventory.sh` only if needed to compose the existing SSIS test and PostgreSQL setup without duplicating fixtures/credentials.

- [ ] RED/GREEN: Simulate termination before/after pointer commit and restart with abandoned staging. No abandoned refresh can become active. Mark/reap abandoned staging through bounded cleanup and delete superseded unreferenced generations, retaining the previous complete inventory after failed scans. Do not run a new scheduler.
- [ ] Run WSL and non-root SDK-container A-11 tests against the separately approved deployed fixture plus isolated PostgreSQL. Verify inventory survives backend restart, source fixture remains unchanged and both known/unknown-format observations retain truthful eligibility. Use unit tests for unprovisioned format/race scenarios; never mutate a real fixture implicitly to manufacture them.
- [ ] Require zero failed/skipped real acceptance tests; preserve six-case A-10 evidence. Run migration/readiness, A-09 encrypted-secret, Foundation, architecture, full Release and audit regressions. No ordinary CI dependency on private SSISDB.
- [ ] Present complete migration/diff, raw DB/redaction evidence, failed-refresh preservation, access-context and crash-boundary evidence for A-11b and parent A-11 review.

## Verification commands and evidence

Run relevant focused projects first, then the existing repository commands:

```sh
dotnet restore FlowPlane.slnx --disable-parallel -m:1 --nologo
dotnet build FlowPlane.slnx --configuration Release --no-restore --nologo -m:1
dotnet test FlowPlane.slnx --configuration Release --no-restore --nologo -m:1
dotnet format FlowPlane.slnx --verify-no-changes --no-restore
dotnet package list --project FlowPlane.slnx --vulnerable --include-transitive
sh tests/documentation/foundation-contract.sh
docker compose --env-file deploy/compose/.env.example -f deploy/compose/compose.yaml config
git diff --check
```

For real SSIS tests, use the ignored private configuration and the WSL/non-root SDK-container commands in [SSIS integration testing](../ssis-integration-testing.md), filtering `RealSsisDiscoveryTests` and `RealSsisValidationTests` separately. Never print secrets, connection strings or locators. Ordinary gated skips are not real acceptance evidence. A-11b additionally runs isolated PostgreSQL persistence and the existing A-09/migration contracts; no live SSIS environment is needed for ordinary Foundation CI.

## Review and commit gates

1. **Current gate:** Review this documentation/issue reconciliation and the three-file documentation diff. Preserve all other dirty/untracked work. No implementation or commit is authorized now.
2. **A-11a gate:** Explicitly authorize its refactor; execute RED/GREEN in the existing checkout. External fixture deployment/configuration requires separate explicit approval. Review the exact resulting diff and real evidence before accepting the child.
3. **A-11b gate:** Only after A-11a acceptance, review final storage contract, migration, access-context and recovery semantics, then explicitly authorize implementation. Schema review occurs before migration application.
4. **Commit gates:** Each reviewed change set needs explicit commit approval. Select only approved paths/hunks; never include unrelated or unreviewed uncommitted work, and never use blanket staging. No automatic commits prescribed by the skill override these gates.
5. **Parent gate:** A-11 is not complete until both children and representative WSL/container evidence pass. Issue closure requires a separate instruction. A-12 needs its own authorization and is not started here.
6. **Future deployment:** Editing-baseline versus live-revision conflict detection and its preflight/deploy race are deferred in technical-design section 34; no implementation belongs to either child.
