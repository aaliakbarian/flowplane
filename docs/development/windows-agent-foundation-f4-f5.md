# Windows Agent Foundation F4/F5 — software review

This batch implements the real TLS/pairing boundary and outbound control-plane polling described by [ADR-0007](../adr/0007-windows-agent-security-transport-and-durable-operations.md) and the [approved foundation plan](../superpowers/plans/2026-11-01-windows-agent-foundation.md). It builds on the accepted [F2 local foundation](windows-agent-foundation-f2.md) and [F3 transactional stores](windows-agent-foundation-f3.md). It does not authorize operational provisioning or certify Windows runtime behavior.

## Activation boundary

Agent control is disabled by default. Ordinary FlowPlane startup still needs no agent tables or agent certificate. Enabling it requires the separately approved F3 migration and a separately provisioned server TLS identity; the application never applies migrations.

The strict `FlowPlane:AgentControl` configuration has these keys:

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Explicitly enable agent control and startup pin loading. |
| `BrowserPort` | `8080` | Existing human/API listener, distinct from the agent port. |
| `BrowserHttps` | `false` | Serve the human listener over the explicit server certificate when configured. Enrollment administration requires actual HTTPS. |
| `AgentPort` | `8443` | Dedicated Kestrel HTTPS listener. No HTTP alternative. |
| `ServerCertificatePath` | unset | Absolute external passwordless PFX path, with private key, server-authentication EKU, SAN and current validity. Never reuse the Data Protection root. |

Enabling explicit listeners replaces URL-based binding with these two configured ports. Listener/publication addresses, SANs, restricted certificate mounts, server trust and Nginx HTTPS upstream must be reviewed before runtime activation. Do not assume that a TLS-terminating proxy makes a plain HTTP upstream eligible for enrollment: no forwarded-proto or certificate-header authentication is enabled. Current HTTP-only development UI deliberately cannot pair agents. No Compose, host trust, certificate or normal database change is performed by this software batch.

The agent listener is identified from its actual accepted Kestrel socket endpoint, not Host, forwarded-port, forwarded-proto or certificate headers. It serves only `/agent/v1/*`. Human endpoints cannot be reached there, and agent endpoints cannot be reached through the human listener. Client certificates are optional only for the bootstrap claim connection; a presented unknown certificate is rejected, not downgraded to anonymous.

## Certificate and authorization boundary

TLS verifies possession. A bounded committed pin snapshot admits only exact DER bytes whose SHA-256 pin and strict self-signed profile match. The shared profile verifies RSA-3072/SHA-256, genuine self-signature, validity, non-CA status, explicit client-authentication EKU and digital-signature usage. Neither subject names nor shared public keys grant authority.

The official ASP.NET Core certificate handler is configured for self-signed client certificates with use/validity checks and no certificate-authentication cache. Platform revocation-list lookup is not the authority for these self-signed identities: each RPC reads current PostgreSQL credential, revocation, agent, assignment and provider/credential-version context. F3 rechecks mutations transactionally. An existing TLS connection is not an authorization cache. A stale handshake snapshot cannot override database revocation. Certificate-handler exception/subject logging is suppressed; boundary errors return allowlisted codes without request bodies or raw exceptions.

Pins load before an enabled service becomes ready and refresh after claim, confirmation, rotation and revocation. A singleton serializes the complete read and publication; a newer refresh obligation, including a cancelled waiter, prevents an older read from reopening admission. Pending/failed refresh clears admission and makes enabled `/health/ready` unhealthy. An existing TLS connection receives retryable sanitized 503 when required refresh interrupts authentication, including a refresh that completes during the authority read; no endpoint authority is granted. Genuine invalid/revoked current credentials remain 403. The existing hosted loader retries with ten-second read deadlines and backoff bounded to 15 seconds, without an operator mutation or restart. Disabled readiness is unchanged. The initial topology remains one backend replica. Read snapshots are short, bounded, read-only REPEATABLE READ transactions and never span network operations. No schema change is added in F4/F5.

## Enrollment and operator UX

All human `/agents` routes retain the existing exact issuer/subject JWT trusted-operator policy. Pairing additionally binds the original issuing actor, provider/version, grant, exact certificate, challenge and ETag. The browser uses same-origin `/api/agents` routes and actual HTTPS.

| Human route | Contract |
| --- | --- |
| `POST /agents/pairing-grants` | Provider UUID, decimal-string version, client request UUID, display name; optional existing Agent UUID/version for explicit re-enrollment. Returns safe grant facts, strong ETag and one transient code only to the successful original issue invocation. |
| `GET /agents/{agentId}/pairings/{grantId}` | Original actor/resource-scoped status, version/ETag and a verification phrase after claim. No code, certificate, nonce or locator. |
| `POST /agents/{agentId}/pairings/{grantId}/confirmation` | Explicit matching phrase and strong If-Match. Missing precondition is 428; stale context is 412. No automatic confirmation. |
| `GET /agents/{agentId}` | Safe health, freshness, versions, captured provider assignments and current authority. SQL execution readiness remains `Unknown`. |
| `POST /agents/{agentId}/revocation` | Operator/resource authorization and strong Agent If-Match. Disabling or unallowlisting the provider does not block security revocation. |

A 256-bit code is generated transiently; only its hash enters F3. A lost issue reply can recover the original receipt with the same request UUID, but never redisplay the code. The UI retains that UUID only in component memory, explicitly warns before losing uncertain context, and never silently creates a fresh grant after a network error. A deliberate reissue reads the same Agent's current version, uses a new UUID and explicitly replaces the pending pairing. Reload recovery of human grant administration is not a persisted browser-secret subsystem.

The protected local helper prompts for the HTTPS origin, non-secret grant UUID and hidden code; there are no command-line secrets or manual CSR/fingerprint steps. Legacy local-setup v1 behavior remains intact; the additional bounded v2 request supplies the grant identity and returns only safe phrase/stage facts. Local ACL, caller/server token verification and bounded receipt acknowledgement remain F2 boundaries.

The agent persists its claim/proof request UUIDs before transmission, never the code or private key. Bootstrap uses a separate HTTPS handler without a client certificate. Pending-certificate proof and status use mTLS. An exact-certificate claim-receipt recovery route supplies the original durable nonce/claim version after a lost claim reply. The operator compares the local and browser phrase and explicitly confirms activation. Replays never select a second identity, grant or native operation implicitly.

For an explicit fresh grant replacing a pending (claimed or proven) enrollment, run the same local helper with the new grant/code on a settled installation. It stages a fresh protected key in a durable per-grant slot, preserves the installation and operation journal and retains the previous identity/enrollment evidence. F3's revoked-pin tombstones remain enforced. The same grant reopens its same staged key; a missing/partial key intent is ActionRequired, not permission to allocate another. Pending claim/proof ACK loss reuses saved IDs and that key. The active identity pointer changes only after operator-confirmed redemption. Retained historical enrollment is validated independently of later live rotation/session state; only an unfinished replacement waits, so subsequent normal rotation, paired status and polling continue after reopen. Wrong-code, stale-grant and cancelled/expired pending-grant rejection can be followed by an explicit fresh grant; corrupt/failed storage or unresolved operation evidence remains closed. This is not emergency recovery for an active revoked, missing or expired identity.

The minimal UI clears transient codes after claim, error, expiry, logout, provider/version/session changes and unmount. It clears protected status on authorization failure and rejects late responses. No secret goes into storage, URLs, telemetry, screenshots, traces or HAR. JavaScript strings are transient but cannot be guaranteed zeroized.

## Closed agent protocol

All routes below are POST unless marked GET, use strict versioned protocol DTOs, reject unknown/duplicate properties, and are bounded to 64 KiB/depth eight. Certificate DER is bounded to 16 KiB. Int64 values are canonical decimal strings. No endpoint accepts arbitrary SQL, shell, PowerShell, plugin commands or native execution payloads.

| Agent route | Authority and result |
| --- | --- |
| `/agent/v1/pairing-claims` | No-client-certificate bootstrap, live code hash and exact public DER. Globally bounded admission. |
| `GET /agent/v1/pairings/{claimId}/claim` | Exact bound pending/current certificate only; immutable original claim receipt, including rotation recovery. |
| `/agent/v1/pairings/{claimId}/proof` | Exact bound pending/current key, recorded challenge, stable proof UUID. |
| `GET /agent/v1/pairings/{claimId}` | Exact bound certificate, safe receipt/phrase. |
| `GET /agent/v1/session` | Active current authority; current fence, database time and lease without acquiring/renewing it. |
| `GET /agent/v1/assignments` | Active current authority; at most 32 public provider UUID/version bindings for controlled rotation. No endpoint or credential material. |
| `/agent/v1/sessions` | Current certificate/context, boot/request UUID, expected fence and actual local-journal readiness. F3 atomically admits or denies. |
| `/agent/v1/polls` | Current session/fence/epoch, monotonic sequence, exact request UUID and bounded readiness/evidence. Returns typed `None` work only. |
| `/agent/v1/rotations` | Old active key supplies a new public certificate under a durable request/claim UUID and current provider/version. |
| `/agent/v1/rotations/{grantId}/proof` | New key proves possession; F3 CAS activation and immutable replay. |
| `GET /agent/v1/rotations/{grantId}` | Bound new key or the old receipt-only key, within its allowed lifetime. |

The HTTP boundary limits concurrent RPCs and bootstrap admission without an unbounded per-IP cache. RPCs have ten-second deadlines, bounded bodies and `no-store` responses. The client disables redirects, cookies and HTTP fallback; it uses platform chain/SAN/lifetime/server-authentication validation. Explicit custom-root trust is narrow and does not install system trust.

## Polling, rotation and recovery

Only one request is in flight. Agent connect/poll IDs and original immutable receipts are flushed in bounded, exclusive, non-secret local control state before advancing. Writes use a flushed same-directory replacement; corrupt, partial, missing expected state or failed writes become ActionRequired, never a silent reset. The existing native-operation journal is not repurposed as a command queue and its unresolved evidence remains preserved.

The database grants 60-second leases. Polling uses five-second jittered idle delays, heartbeat intervals no greater than 15 seconds and bounded retry backoff. Lost responses replay exact request IDs/bodies. Duplicate acknowledgements do not extend the lease. Reordered, changed, future or mismatched receipts fail closed. A restarted process uses a new boot identity, probes database time and waits an existing lease with local monotonic elapsed time; it does not assume graceful shutdown released authority. Replaying an uncommitted prior-boot connect can create an old lease, so the agent probes again before admitting the new boot.

Unknown or unresolved journal state prevents acquisition. A declared settled recovery digest is only a control-plane self-report, never permission to invoke native operations. F3's current assignment, provider, managed-credential and authorization versions remain authoritative. A revoked continuing connection is denied on its next RPC. Ambiguous native operations are not retried, and this foundation contains no native dispatch implementation. An expired never-committed poll may require ActionRequired rather than discarding the request and guessing a new sequence.

Rotation stages a fresh protected CNG key only after a durable local intent. Ambiguous responses recover the same new key/claim, not another key. Missing-claim recovery first uses the pending certificate's immutable receipt route; if the pending pin is not yet admitted (including a never-arrived POST or committed BeginRotation before Claim), it replays the exact saved original POST through the still-active old certificate. Claim/proof/activation ACK loss retains the pending-certificate path, IDs and public DER. The new key proves possession before atomic server activation; the old key becomes receipt-only. Local promotion is recoverable after a crash and preserves old key/evidence. The worker uses authenticated current assignment metadata rather than inventing a provider version. Expired credentials, missing keys, inconsistent promotion evidence and unresolved native work fail closed. Actual CNG/SID/ACL behavior still requires separately approved Windows testing.

AgentHealth covers authenticated connectivity, last contact/heartbeat, package/protocol version, control readiness, authority/freshness and recovery state. It does not certify service-account SQL permissions, native execution readiness, host health or SQL-host health. HostObservations and DataFlowTelemetry remain separate PoCs, not a telemetry pipeline or UI dashboard.

## Verification and remaining gates

Task-level RED/GREEN evidence, exact inventory and final command results are recorded in the batch review report. Distinguish these evidence classes:

- Portable unit/fault tests and real temporary-file reopen checks prove the tested software boundaries, not Windows ACLs/CNG or PostgreSQL races.
- Real ephemeral loopback Kestrel tests prove TLS, server trust/SAN, physical listener isolation and continuing-connection revocation against a controlled current-authority double. They do not replace real database acceptance.
- Frontend containerized component and intercepted-HTTP Chromium tests use synthetic identities/contracts. They are not real Keycloak, Windows enrollment or Nginx TLS acceptance.
- Prior F3 isolated PostgreSQL acceptance remains separate evidence. This batch does not reapply its migration or rerun the large database matrix.

Still require separate authorization for normal development F3 migration, development server TLS/SAN/trust/Nginx configuration, persistent agent identity provisioning, and actual Windows install/token/CNG/pipe/pair/restart/rotate/revoke tests. Combined real PostgreSQL plus TLS/enrollment/polling race acceptance is not claimed by software doubles. No skipped or declined check is recorded as passed. Previously declined standalone cryptographic/log probes remain unexecuted. B-02 native execution proof and F6 packaging/download/full setup UI are not implemented or certified here.
