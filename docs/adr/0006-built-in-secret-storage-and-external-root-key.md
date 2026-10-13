# ADR 0006: Provide Built-in Encrypted Secret Storage with External Root-Key Protection

- **Status:** Accepted
- **Supersedes:** None
- **Superseded by:** None

## Context

FlowPlane Core must securely handle provider credentials even in Minimal FlowPlane. External enterprise secret managers are optional integrations and must not be prerequisites for using FlowPlane with an SSIS provider.

FlowPlane may manage multiple `ProviderInstance` records, each with independent credentials. For example, production and test SSIS instances may use different SQL logins and passwords. A single global SSIS password therefore cannot represent the product credential model. Plaintext credentials must not be persisted as `ProviderInstance` configuration or as connection strings.

[ADR-0003](0003-core-independence-and-progressive-integration.md) established that secure credential and `SecretReference` handling is required while external enterprise secret-management integration remains optional. It deliberately left the concrete secret backend undecided. This ADR resolves that architectural question for Minimal FlowPlane.

## Decision

The proposed decision is that Minimal FlowPlane provides provider-independent secret references and a built-in encrypted secret backend protected by root material held outside PostgreSQL.

### SecretReference

A `ProviderInstance` references credential material through `SecretReference`. The initially supported architectural scheme families are:

- `flowplane:<opaque-id>` — a built-in FlowPlane-managed secret;
- `env:<variable-name>` — an externally supplied secret intended for development, testing, or bootstrap scenarios.

Future enterprise schemes, such as Vault or cloud secret managers, may be added as separate Secret Integrations without changing `ProviderInstance` semantics. This ADR does not define those future integrations.

### Built-in FlowPlane secret backend

Minimal FlowPlane provides a built-in encrypted secret store. Provider credentials may be encrypted and stored in FlowPlane's PostgreSQL database.

A `ProviderInstance` stores only its `SecretReference`. A built-in secret record stores authenticated ciphertext and required protection metadata, never plaintext provider credentials.

For the initial SSIS SQL-authentication use case:

- the username remains ordinary non-secret provider configuration;
- the password is secret material referenced through `SecretReference`.

### Key hierarchy

The built-in store uses two levels of protection conceptually:

```text
External root/key-encryption material
        |
        v
ASP.NET Core Data Protection key ring
        |
        v
Encrypted FlowPlane secret records
```

The Data Protection key ring may be persisted in FlowPlane PostgreSQL. A persisted key ring must be protected at rest by external root/key-encryption material that is not stored in PostgreSQL. PostgreSQL may therefore contain encrypted secret records and a protected key ring, but a PostgreSQL backup alone must be insufficient to decrypt provider credentials.

### Cryptography direction

The initial built-in store uses ASP.NET Core Data Protection rather than a FlowPlane-designed encryption protocol. This choice provides:

- authenticated protection;
- framework-managed payload and key formats;
- automatic key lifecycle and rolling;
- purpose isolation;
- support for a shared key ring across later backend replicas; and
- supported X.509 protection of persisted key-ring material.

ASP.NET Core Data Protection is not primarily designed as a general-purpose indefinite secret-storage system. FlowPlane deliberately accepts it for the initial built-in provider-secret backend because:

- the protected material is application-owned and consumed only by FlowPlane;
- the Data Protection key ring is durably persisted;
- old Data Protection keys are retained so existing provider credentials remain decryptable;
- persisted keys are explicitly protected at rest by external root material;
- backup and recovery preserve both the protected key ring and required external root certificates; and
- provider credentials support normal replacement and rotation rather than serving as immutable archival secrets.

If future operational, compliance, HSM or KMS, indefinite-retention, or enterprise key-management requirements exceed what ASP.NET Core Data Protection can safely support, this ADR may be superseded by a stronger secret-backend architecture without changing `ProviderInstance` or `SecretReference` semantics.

The built-in secret protector uses stable FlowPlane application isolation and stable, versioned purpose strings. Secret protection incorporates the opaque secret identity in the purpose chain so ciphertext cannot be transparently moved between secret identities.

This ADR does not define exact C# classes or interfaces.

### Key-ring protection

Explicit Data Protection key persistence removes automatic at-rest protection because the framework cannot infer a suitable protection mechanism for the selected repository. Explicit at-rest protection of the persisted key ring is therefore required.

The reference deployment uses an external X.509 certificate and private key for key-ring protection through `ProtectKeysWithCertificate` or its supported successor. Previous root certificates may be retained during rollover through supported multi-certificate unprotection behavior.

This ADR does not define enterprise certificate management.

### Root-key separation

The X.509 private key or equivalent root material:

- must not be stored in PostgreSQL;
- must not be committed to Git;
- must not be logged; and
- must not be returned through an API.

For the containerized reference and development deployment, Docker Compose secrets are the intended bootstrap mechanism. The backend receives only the specific secret material it requires.

This is a reference/development bootstrap mechanism, not an enterprise key-management claim. External production key-management mechanisms remain future deployment and security design.

### Optional secret infrastructure must not become a boot prerequisite

Absence of built-in-secret root material must not prevent ordinary FlowPlane Core startup when no built-in secret operation is required.

```text
No root certificate
    |
    +-- FlowPlane boots
    +-- PostgreSQL remains usable
    +-- authentication remains usable
    +-- provider-independent functions remain usable
    |
    `-- flowplane: secret create/update/resolve
            -> safe secret-protection-unavailable result
```

The implementation must not eagerly turn missing secret-protection material into a global application-startup failure.

### Environment resolver

The `env:` scheme is allowed for:

- development;
- testing; and
- operator-managed bootstrap scenarios.

It is not the normal product credential store. FlowPlane does not persist environment-provided values, and there is no implicit global SSIS password. Different `ProviderInstance` records may independently reference different environment variables.

### Provider independence

The built-in secret record is provider-independent. It contains no:

- SSIS host;
- username;
- connection string;
- provider type; or
- folder, project, or package identity.

`SecretReference` remains a FlowPlane supporting concept rather than an SSIS-specific concept.

### Backup and recovery

A recoverable built-in-secret deployment requires both:

1. PostgreSQL data containing:
   - encrypted FlowPlane secret records; and
   - the protected Data Protection key ring;
2. a separately secured backup of all required external root certificate and private-key material.

A database backup alone must not decrypt provider credentials. Loss of required root material may make stored credentials unrecoverable. Database backups and root-key backups must not be stored together as one unprotected artifact.

### Key rotation

Normal Data Protection key rolling is allowed. Expired Data Protection keys must not be deleted merely because they have expired, because existing ciphertext may still depend on them.

Root-certificate rotation requires an overlap period in which key-ring entries protected by previous certificates remain decryptable. Exact operational re-protection and retirement tooling is deferred.

### Security boundary

This architecture protects provider credentials from database-only or database-backup disclosure. It does not protect credentials from a fully compromised FlowPlane backend process that legitimately has access to root material and can resolve provider credentials.

## Alternatives considered

### Plaintext credentials in PostgreSQL

Persist credentials as ordinary `ProviderInstance` configuration or connection strings. This alternative is rejected because database access or backup disclosure would expose provider credentials directly.

### Environment variables as the primary provider credential architecture

Use process environment variables as the normal provider credential store. This alternative is rejected because:

- multiple `ProviderInstance` records require independently managed credentials;
- the process environment is not an appropriate primary managed credential store; and
- FlowPlane needs durable credential lifecycle without requiring an external enterprise dependency.

The `env:` scheme remains available only as a limited resolver.

### Require Vault, Key Vault, or another external secret manager

Require an enterprise secret-management system before FlowPlane can configure provider credentials. This alternative is rejected for Minimal FlowPlane because optional enterprise integrations must not become prerequisites.

### Custom AES-GCM envelope

Use .NET's AES-GCM primitive and define a FlowPlane encryption envelope. AES-GCM is technically capable of authenticated encryption, but this alternative is rejected for the initial implementation because FlowPlane would own nonce handling, envelope format, key rotation, versioning, migration, replica coordination, and the associated security-review burden.

### Data Protection key ring without explicit at-rest protection

Persist the Data Protection key ring without encrypting it. This alternative is rejected because explicit key persistence requires explicit key encryption at rest to preserve separation between a PostgreSQL backup and the root protection material.

### Data Protection key ring only in an ephemeral container filesystem

Keep the key ring only in the backend container filesystem. This alternative is rejected because container restart or replacement could make long-lived credentials unreliable or unrecoverable and would not provide a durable shared key ring for later replicas.

## Consequences

Positive consequences include:

- Minimal FlowPlane securely supports multiple provider credentials;
- no external enterprise secret manager is required;
- `ProviderInstance` remains free of plaintext secrets;
- database-only disclosure does not reveal provider credentials;
- future external secret resolvers can reuse `SecretReference` semantics; and
- later backend replicas can share a durable protected key ring.

Trade-offs include:

- root-key and certificate backup becomes operationally critical;
- lost root material can make stored credentials unrecoverable;
- root-certificate rotation requires disciplined overlap and eventual migration or retirement procedures;
- built-in encrypted secret storage adds database and security complexity; and
- compromise of a backend process with legitimate root-key access remains sufficient to access credentials.

## Explicitly not decided

This ADR does not decide:

- final secret API routes or DTOs;
- final RBAC for credential mutation;
- Vault, Azure Key Vault, or AWS secret integrations;
- Kubernetes Secrets;
- cloud KMS;
- HSM integration;
- automated provider credential rotation;
- a production certificate authority;
- production certificate-renewal tooling;
- exact secret table or column names;
- exact C# interfaces;
- exact PFX or PEM provisioning workflow; or
- exact root-certificate retirement or re-encryption tooling.

Those matters belong to later technical, security, or deployment design.

## Related documentation

- [ADR 0002: Separate Integration Families](0002-separate-integration-families.md)
- [ADR 0003: Keep FlowPlane Core Independent and Make External Integrations Additive](0003-core-independence-and-progressive-integration.md)
- [FlowPlane MVP Domain Model](../domain/mvp-domain-model.md)
- [FlowPlane SSIS Provider Technical Design](../architecture/providers/ssis-provider-technical-design.md)
- [Milestone A — Development Foundation + SSIS Discovery Implementation Plan](../development/milestones/milestone-a-foundation-and-ssis-discovery.md)
