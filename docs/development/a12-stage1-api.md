# A-12 Stage 1 — secure configuration and validation

This implements only the approved Stage 1 of issue #13. Discovery admission,
inventory browsing, background execution, Gate B and UI remain deferred.

## Execution and review plan

1. RED/GREEN: signed JWT tests for every route, exact operator pairs and endpoint policy.
2. RED/GREEN: strict input, safe responses, optimistic concurrency and configuration validation.
3. RED/GREEN: real PostgreSQL atomic creation/replacement, missing protection and redaction.
4. Full Release, architecture, security/audit and formatting checks; independent review.

No normal-development credential writes are permitted while independent off-host
root-certificate backup remains pending. Test writes use disposable PostgreSQL
databases and synthetic credentials with separate disposable protection material.
No migration or startup migration is needed. No accepted ADR changes are made.

## Operator configuration

`FlowPlane:Operators:Allowed` is an array of exact `Issuer`/`Subject` pairs.
An empty array denies operator access. Malformed entries are rejected at startup.
JWT issuer/audience/signature/lifetime validation remains
mandatory; a valid login alone does not grant access. `/me` remains authenticated
but does not require operator membership. Milestone A is a shared trusted-operator
workspace: every authorized operator can manage every ProviderInstance, with the
policy applied independently on every resource request. IDs confer no authority.

Use configuration keys such as `FlowPlane__Operators__Allowed__0__Issuer` and
`FlowPlane__Operators__Allowed__0__Subject`. To bootstrap development, obtain the
issuer from the existing backend OIDC issuer configuration and the exact subject
from authenticated `/api/me`; never use a username/name claim, wildcard, or a
copied bearer token as configuration.
Do not add a default operator to committed configuration.

`FlowPlane:Ssis:AllowedEndpoints` is an array of exact `Host`/`Port` pairs, e.g.
`FlowPlane__Ssis__AllowedEndpoints__0__Host` and
`FlowPlane__Ssis__AllowedEndpoints__0__Port`. Empty denies all API native access.
Hosts are ASCII DNS names or canonical IPv4 literals, matched ordinally; IPv6,
URIs, instance syntax, embedded ports, wildcards and connection-string fragments
are rejected in this initial API. Ports are integers 1–65535. Only an operator
controlling backend configuration can change this list; trusted DNS/routing must
be maintained by that operator. This is not a DNS-rebinding-resistant network
firewall. The API rechecks the list before configuration writes and validation.
Apply operator/endpoint configuration through backend environment or a private
Compose override and restart the backend; these policies are startup snapshots,
not a hot-reload security-control API. No operators or destinations are enabled
by default. Root-certificate backup must precede real credential onboarding.
Mandatory encryption and certificate validation are unchanged; explicit
`trustServerCertificate: true` is permitted only in Development.

## REST contract

Backend routes below are exposed through the existing Nginx `/api` prefix.
All require a valid access token and the trusted-operator policy.

| Method/path | Input / result |
| --- | --- |
| GET `/provider-instances` | Bounded list, `offset` (default 0), `limit` (default 50, maximum 100) |
| POST `/provider-instances` | Create input below; 201, Location and strong ETag |
| GET `/provider-instances/{id}` | Safe configuration DTO and strong ETag |
| PUT `/provider-instances/{id}` | Same non-secret fields as create, no credential; requires If-Match |
| PUT `/provider-instances/{id}/credential` | `{ "password": "<write-only>" }`; requires If-Match |
| POST `/provider-instances/{id}/validation` | No input; structured A-10 result, no persisted validation state |

Create input: `providerType: "ssis"`, `displayName`, `isEnabled`,
`configuration: { schemaId: "ssis.provider-instance/v1", data: { host, port,
username, trustServerCertificate } }`, and `credential: { password }`.
The existing provider-owned codec validates configuration; unknown input members
are rejected. Passwords are 1–4096 characters, separate from configuration and
never returned. JSON input is bounded to 32 KiB. Request DTO diagnostic strings
are redacted. Password strings cannot be perfectly zeroized in managed memory.

Safe provider DTO: `id`, `providerType`, `displayName`, `isEnabled`, versioned
non-secret `configuration`, `version`, `createdAtUtc`, `updatedAtUtc`. No domain
entity is serialized. No SecretReference or secret locators are accepted/returned.
Provider type is immutable on update. Creation does not contact SQL Server.

ETags are quoted positive decimal ProviderInstance versions (`"1"`). PUT requires
one strong exact version: missing is 428, stale is 412, malformed/weak/wildcard
is 400. Every accepted configuration/credential change increments the version;
credential replacement preserves the owned secret identity. Validation detects
intervening configuration/credential changes rather than returning an obsolete
result. Errors use sanitized Problem Details with stable codes, never exception
messages. Responses use `Cache-Control: no-store`.

Validation returns the existing `ProviderValidationResult`: `outcome`, `checks`
(`code`, `status`, `message`), `capabilities` (`code`, `availability`, `reasonCode`),
allowlisted `facts` (`code`, `value`) and `isValid`. JSON enum values follow the
existing contract: outcome/status 0 = Succeeded, 1 = Failed, 2 = Unavailable;
availability 0 = Available, 1 = Unavailable, 2 = Unknown. A completed validation
request is HTTP 200 even when its structured outcome is Failed/Unavailable.
Policy/context/storage errors remain Problem Details, not native diagnostic text.

For validation, the provider rechecks the captured ProviderInstance version after
resolving its password but before handing that password to the native probe.
The resolved buffer is then fixed for the probe lifetime. This prevents host/config
changes followed by credential rotation from mixing an old destination with a new
credential. The API also checks context after probing; no database transaction or
row lock spans the network operation. The optional guard preserves trusted,
in-memory A-10 callers; persisted HTTP validation always supplies it.
