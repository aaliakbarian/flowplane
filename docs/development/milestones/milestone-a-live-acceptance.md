# Milestone A — Live Acceptance and Closure Review

Evidence date: 2026-10-28. Reviewed source: `main` at `29f67833916c50d8073b69e31fa8db31a9ff602f`.
This records development acceptance, not production readiness or native SSIS execution certification.

## Scope and closure assessment

The [Milestone A plan](milestone-a-foundation-and-ssis-discovery.md),
[parent #1](https://github.com/aaliakbarian/flowplane/issues/1) and
[A-12 #13](https://github.com/aaliakbarian/flowplane/issues/13) require the development
foundation plus secure provider configuration, validation and read-only discovery.
The implemented journey satisfies those product exit criteria. No mandatory
product requirement remains unmet within the approved scope.

GitHub inspection found A-01 through A-11 (#2–#12), including A-11a #14 and
A-11b #15, closed. #13 is the last open issue assigned to Milestone A
(milestone number 1: thirteen closed, one open). Parent #1 is open and is not
assigned to that milestone object. No issue or milestone was closed by this review.
Source task-status fields, checkboxes and corresponding Foundation assertions now
record A-01 through A-12 Complete/checked, following verification of the original
exit criteria and explicit reconciliation approval. GitHub closure is still pending.

## Implemented boundaries and source evidence

| Area | Source / evidence |
| --- | --- |
| Foundation, OIDC, containers and CI | A-01–A-08, closed issues #2–#9; [Foundation run 40465934036](https://github.com/aaliakbarian/flowplane/actions/runs/40465934036) succeeded for the exact reviewed HEAD. |
| Provider configuration and secrets | A-09 `f9872da`; provider-independent configuration/reference boundaries and the [ADR-0006](../../adr/0006-built-in-secret-storage-and-external-root-key.md) encrypted store. |
| SSIS validation | A-10 `3336582`, format correction `9ff60de`, compatibility evidence `78147b0`; [SSIS testing/compatibility record](../ssis-integration-testing.md). |
| Complete read-only scanning | A-11a `32205b5`, closed #14; invocation-scoped awaited sink, internal project batches, current observations and consistency checks. |
| PostgreSQL inventory publication | A-11b `4d15f56`, closed #15 and parent #12; streamed staging, fences and atomic complete-generation publication. |
| Configuration/validation API | A-12 Stage 1 `ffd3b28`; [contract and operator policy](../a12-stage1-api.md). |
| Durable discovery hosting | Stage 2 `1efa288`; [admission, leases and recovery](../a12-stage2-discovery.md). |
| Secure inventory disclosure | Stage 3 `1e4967d`; [generation-bound paging and native visibility checks](../a12-stage3-inventory.md). |
| React operator workspace | Stage 4 `29f6783`; [UI behavior](../a12-stage4-ui.md). |

Discovery scans every currently visible folder/project and approved current
metadata, without SSISDB pagination or total-result truncation. PostgreSQL is the
current observed inventory, not native authority or SSIS version history. Only
compatible entry points have executable references. Browsing pages PostgreSQL
and rechecks targeted native visibility; it does not run another discovery scan.
The single discovery-specific hosted runner is not a workflow scheduler or a
native package-execution worker. These refinements preserve the Accepted ADRs.

## Real live journey

The controlled external environment is SQL Server 2025 RTM `17.0.1000.7`, with
the separately approved `FlowPlaneA10/FlowPlaneA11Fixture` project. Project format
`1` and package format `8` remain separate compatibility facts. All SQL 17.x
builds are eligible under the major-version policy subject to live catalog checks;
only `17.0.1000.7` has the recorded independent representative evidence.
SQL 2019/2022 remain Unverified. No additional build, package-format or native
runtime certification is implied.

| Check | Observed result |
| --- | --- |
| Real authentication and authorization | Keycloak Authorization Code + S256 PKCE; exact issuer/subject operator membership. Anonymous protected routes returned 401. Gate L1 separately proved authenticated non-operator 403 and authenticated `/me` 200. |
| Single UI onboarding | Exactly one disposable `Milestone A acceptance` ProviderInstance, HTTP 201; write-only controlled test password and strong ETag. |
| Encrypted storage | Exactly one linked managed secret, authenticated ciphertext rather than plaintext; X.509-protected PostgreSQL key ring and its existing fingerprint preserved. No secret locator returned. |
| Live validation | Reachability, encrypted connection, SQL authentication, public catalog/version/format and capability checks succeeded. Discovery and execution-permission availability were Available; no execution occurred. |
| Durable refresh | UUIDv7 admission returned HTTP 202 / Accepted. Same-key replay returned the same receipt; browser reload recovered it while Accepted, then observed Published. |
| Atomic publication | One complete Compatible/Succeeded generation: 10 observations and 2 candidates. Current-generation pointer and durable counts agree; no in-flight generation, retained slot or publication uncertainty. |
| Native-checked browsing | `EntryPoint.dtsx` eligible; `InternalStep.dtsx` observed but ineligible. Required/sensitive project flags, package `BatchSize` association and relative environment-reference metadata verified without values. Int64 identifiers/revisions remained decimal strings. |
| Development frontend | Real login, configuration, validation, discovery/reload and inventory through Nginx/Vite/HMR passed. |
| Static frontend | Fresh real login and inventory browsing reused the same provider/generation; compiled assets served by Node-free Nginx. The backend remained Development, not a production deployment. |
| Optional live pagination | Read-only `limit=1` requests traversed ten one-item pages in the same generation. First-page nextCursor was present; the complete identity set matched a bounded reference read, with no duplicates, missing identities or sensitive values. No new provider, credential or refresh. |

SQL encryption remained Mandatory. `TrustServerCertificate=true` was the
explicitly authorized Development-only exception for this controlled fixture;
it is not evidence of CA/hostname validation and does not change production defaults.

No requests were intercepted or mocked. Playwright tracing, HAR, video and
screenshots were disabled. Inspected API responses, browser console/DOM and safe
reports contained no passwords, bearer tokens, secret locators, connection
strings, parameter defaults or environment values. Passwords were absent from
browser storage. OIDC token exchange and approved session-scoped token storage
remain intentional; tokens were not exported as test artifacts. No
production-grade token isolation is claimed.

An initial automation attempt and its read-only diagnostic stopped on the
private driver's exact-label selector before inventory HTTP access. Selecting the
unique real combobox resolved it; all original metadata/security assertions then
passed. No application change, duplicate provider or duplicate discovery resulted.
The original development Nginx was restored; backend, Keycloak, PostgreSQL and
volume identities, the external PFX and existing protected key ring were preserved.
Four migrations/seven public tables remain, with one disposable provider/secret
and one published generation retained for review.

## Evidence distinctions and unexecuted checks

Earlier accepted stage reviews recorded the Release/architecture/security suites,
NuGet auditing and real WSL/non-root SDK-container regressions (25 SSIS tests in
each path at Stage 4). Those suites were not rerun for this documentation review.
Earlier browser tests with synthetic sessions and mocked HTTP contracts are
component/contract evidence, not live Keycloak/operator/SSISDB acceptance. The
real journey above supplies that separate evidence; no skipped test is counted
as acceptance.

The two previously declined standalone checks remain **Not Executed**:

- post-restart existing-key cryptographic usability probe;
- post-restart backend sensitive-log inspection.

Feature-level encryption, credential resolution and browser/API redaction checks
do not retrospectively execute or replace those standalone probes.

## Disposable development data and deployment readiness

ProviderInstances, credentials and inventory created for this local acceptance
are disposable. Loss of the development database, PFX or key ring may require
recreating test data and re-entering credentials; that loss is explicitly accepted.
Independent off-host PFX backup, a backup before every test and an additional
acceptance database are not Milestone A development prerequisites. Use the existing
`flowplane` development database. This does not authorize deleting/regenerating
existing root material or relaxing encryption, operator authorization, endpoint
allowlisting, permissions or secret-redaction controls.

For non-disposable/production deployment readiness, [ADR-0006 backup and recovery](../../adr/0006-built-in-secret-storage-and-external-root-key.md#backup-and-recovery)
still requires separately secured required root certificates/private keys and
PostgreSQL backups containing the protected key ring and encrypted secrets.
Independent off-host recovery preparation remains pending and is not claimed as
completed. Production identity/TLS, topology and operational recovery acceptance
are separate work, not new Milestone A closure gates.

No Pipeline registration, Run/RunAttempt, SSIS execution/cancellation/deployment,
SSISDB permission mutation, historical-version management or graph parsing was
introduced or exercised. No production certification is claimed.

## Administrative closure gate

Product evidence supports closing #13, then parent #1, and marking milestone 1
closed after explicit approval. Source task-state reconciliation is complete;
it does not itself close GitHub issues or the milestone.
Review/commit the approved documentation and verify Foundation for the resulting
HEAD before publishing completion comments. No closure command has been executed.
