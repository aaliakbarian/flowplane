# A-12 Stage 4 — minimal operator UI

## Implementation plan

**Spec:** approved `END_OF_A12_STAGE4_REQUEST`, and the authoritative
[Stage 1](a12-stage1-api.md), [Stage 2](a12-stage2-discovery.md) and
[Stage 3](a12-stage3-inventory.md) contracts. No backend/schema redesign.

**Goal:** configure, validate, refresh and browse SSIS providers in the existing
authenticated React application, without registration or execution.

**Architecture:** four small functional components, one same-origin API client,
component-local state, and session-scoped non-secret refresh recovery records.
Reuse React/TypeScript/Vite/Vitest/OIDC pins; no new dependencies.

1. RED/GREEN API and UUIDv7 tests: strict strong ETags, write-only password bodies,
   safe errors, RFC 9562 vector, version/variant/time boundaries and Web Crypto.
2. RED/GREEN workspace/form/validation tests: operator denial, configuration and
   credential writes, clearing password inputs, stale If-Match and separate checks.
3. RED/GREEN refresh/inventory tests: save key before admission, ambiguous retry,
   reload recovery/polling, Retry-After, terminal/uncertain states, bounded inventory
   pages, cursor invalidation and no stale disclosure after any failure/switch.
4. Container-only typecheck, all Vitest tests and production build; development
   Nginx/Vite and static Nginx browser tests with intercepted synthetic API traffic.
   Backend/architecture/security regressions and audit; one independent review.

## Review focus and boundaries

- Late responses after provider/account/logout changes must not restore old data.
- Passwords exist only in password controls and ephemeral request construction,
  never React state, browser storage, URLs, logs or recovery records.
- Recovery retains the original UUIDv7 and If-Match after uncertain admission.
  A rejected request is not silently replaced by a different request. A later
  endpoint/provider rejection cannot authorize discarding an uncertain key.
  Only the first, definitively rejected attempt for a freshly generated key is
  discardable; reloaded keys are always treated as potentially accepted.
- Status polling is serial and honors Retry-After across phase transitions.
- Identity endpoint 401 clears the visible session and offers sign-in again,
  preserving only non-secret refresh recovery for the same subject.
- A transient identity-bootstrap error supports one manual authenticated retry per
  action. The retry clears identity state, does not restore old provider data,
  still requires the protected operator-list check, and ignores late responses
  after session expiry. No authentication framework or automatic retry loop.
- Inventory failures clear the entire page. Only cursor expiry/restart/supersession
  can restart an existing continuation at page one; native mismatches need refresh.
- Browser navigation stops status observation, not accepted backend work.

The implementation introduced no host Node/npm, UI frameworks, migrations or
native mutations and did not run the declined backend probes or change Accepted
ADRs. Its mocked browser tests were not live acceptance. The separately approved
[real live journey](milestones/milestone-a-live-acceptance.md) subsequently used
one disposable development provider/credential in both Nginx modes. Independent
off-host recovery remains future deployment-readiness work, not a disposable
development acceptance gate. No implicit authorization for more credential
onboarding, external mutations, commits or issue closure follows from this guide.

UUIDv7 follows the [RFC 9562 layout and independent example vector](https://www.rfc-editor.org/rfc/rfc9562.html#section-5.7).
Only its 48-bit timestamp and version/variant fields are set; remaining bits come
from browser Web Crypto, never Math.random or crypto.randomUUID (UUIDv4).
