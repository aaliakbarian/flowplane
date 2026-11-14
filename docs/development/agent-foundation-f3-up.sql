START TRANSACTION;
CREATE TABLE "Agents" (
    "Id" uuid NOT NULL,
    "DisplayName" character varying(200) NOT NULL,
    "State" character varying(32) NOT NULL,
    "Version" bigint NOT NULL,
    "AuthorizationVersion" bigint NOT NULL,
    "SessionFence" bigint NOT NULL,
    "CurrentBootId" uuid,
    "ConnectRequestId" uuid,
    "SessionId" uuid,
    "ConnectDigest" bytea,
    "ConnectAcceptedAtUtc" timestamp with time zone,
    "ConnectLeaseExpiresAtUtc" timestamp with time zone,
    "ConnectAuthorizationVersion" bigint,
    "ConnectFence" bigint,
    "LeaseAuthorizationVersion" bigint,
    "LeaseExpiresAtUtc" timestamp with time zone,
    "PollRequestId" uuid,
    "PollDigest" bytea,
    "PollSequence" bigint NOT NULL,
    "PollAcknowledgedAtUtc" timestamp with time zone,
    "PollLeaseExpiresAtUtc" timestamp with time zone,
    "PollFence" bigint,
    "PollAuthorizationVersion" bigint,
    "NextPollSeconds" integer,
    "ProtocolVersion" integer,
    "PackageVersion" character varying(64),
    "Readiness" character varying(32) NOT NULL,
    "RecoveryRequired" boolean NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "LastContactAtUtc" timestamp with time zone,
    "LastHeartbeatAtUtc" timestamp with time zone,
    "RevokedAtUtc" timestamp with time zone,
    "RevokedByIssuer" character varying(2048) COLLATE "C",
    "RevokedBySubject" character varying(512) COLLATE "C",
    CONSTRAINT "PK_Agents" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_Agents_ConnectRequestId" CHECK ("ConnectRequestId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_Agents_CurrentBootId" CHECK ("CurrentBootId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_Agents_Display" CHECK (length("DisplayName") BETWEEN 1 AND 200),
    CONSTRAINT "CK_Agents_Id" CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_Agents_Lifetime" CHECK ("UpdatedAtUtc" >= "CreatedAtUtc"),
    CONSTRAINT "CK_Agents_Package" CHECK (num_nonnulls("ProtocolVersion","PackageVersion") IN (0,2) AND ("ProtocolVersion" IS NULL OR ("ProtocolVersion" = 1 AND length("PackageVersion") BETWEEN 1 AND 64))),
    CONSTRAINT "CK_Agents_Poll" CHECK (num_nonnulls("PollRequestId","PollDigest","PollAcknowledgedAtUtc","PollLeaseExpiresAtUtc","PollFence","PollAuthorizationVersion","NextPollSeconds") IN (0,7) AND (("PollSequence" = 0 AND "PollRequestId" IS NULL) OR ("PollSequence" > 0 AND "PollRequestId" IS NOT NULL AND "SessionId" IS NOT NULL AND octet_length("PollDigest") = 32 AND "PollFence" = "ConnectFence" AND "PollAuthorizationVersion" = "ConnectAuthorizationVersion" AND "NextPollSeconds" = 5 AND "PollLeaseExpiresAtUtc" > "PollAcknowledgedAtUtc"))),
    CONSTRAINT "CK_Agents_PollRequestId" CHECK ("PollRequestId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_Agents_Revocation" CHECK (num_nonnulls("RevokedAtUtc","RevokedByIssuer","RevokedBySubject") IN (0,3) AND (("State" = 'Revoked') = ("RevokedAtUtc" IS NOT NULL))),
    CONSTRAINT "CK_Agents_Session" CHECK (num_nonnulls("CurrentBootId","ConnectRequestId","SessionId","ConnectDigest","ConnectAcceptedAtUtc","ConnectLeaseExpiresAtUtc","ConnectAuthorizationVersion","ConnectFence","LeaseAuthorizationVersion","LeaseExpiresAtUtc") IN (0,10) AND ("SessionId" IS NULL OR (octet_length("ConnectDigest") = 32 AND "ConnectFence" > 0 AND "ConnectFence" <= "SessionFence" AND "ConnectAuthorizationVersion" > 0 AND "LeaseAuthorizationVersion" = "ConnectAuthorizationVersion" AND "ConnectLeaseExpiresAtUtc" > "ConnectAcceptedAtUtc" AND "LeaseExpiresAtUtc" >= "ConnectLeaseExpiresAtUtc" AND "ProtocolVersion" IS NOT NULL))),
    CONSTRAINT "CK_Agents_SessionId" CHECK ("SessionId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_Agents_States" CHECK ("State" IN ('Pending','Active','Quarantined','Revoked') AND "Readiness" IN ('Unknown','ReadyForControlPlane','Unavailable','ActionRequired')),
    CONSTRAINT "CK_Agents_Versions" CHECK ("Version" > 0 AND "AuthorizationVersion" > 0 AND "SessionFence" > 0 AND "PollSequence" >= 0)
);

CREATE TABLE "AgentCredentials" (
    "Id" uuid NOT NULL,
    "AgentId" uuid NOT NULL,
    "Der" bytea NOT NULL,
    "DerSha256" bytea NOT NULL,
    "SpkiSha256" bytea NOT NULL,
    "State" character varying(32) NOT NULL,
    "Version" bigint NOT NULL,
    "NotBeforeUtc" timestamp with time zone NOT NULL,
    "NotAfterUtc" timestamp with time zone NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ProvedAtUtc" timestamp with time zone,
    "ActivatedAtUtc" timestamp with time zone,
    "RetiringUntilUtc" timestamp with time zone,
    "RevokedAtUtc" timestamp with time zone,
    CONSTRAINT "PK_AgentCredentials" PRIMARY KEY ("Id"),
    CONSTRAINT "AK_AgentCredentials_AgentId_Id" UNIQUE ("AgentId", "Id"),
    CONSTRAINT "CK_AgentCredentials_AgentId" CHECK ("AgentId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentCredentials_Der" CHECK (octet_length("Der") BETWEEN 1 AND 16384),
    CONSTRAINT "CK_AgentCredentials_Digests" CHECK (octet_length("DerSha256") = 32 AND octet_length("SpkiSha256") = 32),
    CONSTRAINT "CK_AgentCredentials_Id" CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentCredentials_Lifecycle" CHECK (("State" NOT IN ('Active','RetiringReceiptsOnly','Retired') OR ("ProvedAtUtc" IS NOT NULL AND "ActivatedAtUtc" IS NOT NULL AND "ActivatedAtUtc" >= "ProvedAtUtc")) AND ("State" <> 'RetiringReceiptsOnly' OR ("RetiringUntilUtc" IS NOT NULL AND "RetiringUntilUtc" > "ActivatedAtUtc")) AND (("State" = 'Revoked') = ("RevokedAtUtc" IS NOT NULL))),
    CONSTRAINT "CK_AgentCredentials_State" CHECK ("State" IN ('PendingProof','Active','RetiringReceiptsOnly','Retired','Revoked') AND "Version" > 0),
    CONSTRAINT "CK_AgentCredentials_Validity" CHECK ("NotAfterUtc" > "NotBeforeUtc"),
    CONSTRAINT "FK_AgentCredentials_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "AgentProviderAssignments" (
    "ProviderInstanceId" uuid NOT NULL,
    "AgentId" uuid NOT NULL,
    "State" character varying(32) NOT NULL,
    "Version" bigint NOT NULL,
    "ProviderVersion" bigint NOT NULL,
    "ProviderCredentialVersion" bigint,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AgentProviderAssignments" PRIMARY KEY ("ProviderInstanceId"),
    CONSTRAINT "AK_AgentProviderAssignments_ProviderInstanceId_AgentId" UNIQUE ("ProviderInstanceId", "AgentId"),
    CONSTRAINT "CK_AgentProviderAssignments_AgentId" CHECK ("AgentId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentProviderAssignments_ProviderInstanceId" CHECK ("ProviderInstanceId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentProviderAssignments_State" CHECK ("State" IN ('Pending','Active','Suspended','Revoked')),
    CONSTRAINT "CK_AgentProviderAssignments_Versions" CHECK ("Version" > 0 AND "ProviderVersion" > 0 AND ("ProviderCredentialVersion" IS NULL OR "ProviderCredentialVersion" > 0) AND "UpdatedAtUtc" >= "CreatedAtUtc"),
    CONSTRAINT "FK_AgentProviderAssignments_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentProviderAssignments_ProviderInstances_ProviderInstance~" FOREIGN KEY ("ProviderInstanceId") REFERENCES "ProviderInstances" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "AgentSecurityEvents" (
    "Id" uuid NOT NULL,
    "AgentId" uuid NOT NULL,
    "CredentialId" uuid,
    "GrantId" uuid,
    "Kind" character varying(32) NOT NULL,
    "Issuer" character varying(2048) COLLATE "C",
    "Subject" character varying(512) COLLATE "C",
    "AuthorizationVersion" bigint NOT NULL,
    "Fence" bigint NOT NULL,
    "PreviousAuthorizationVersion" bigint NOT NULL,
    "PreviousFence" bigint NOT NULL,
    "RecordedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AgentSecurityEvents" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_AgentSecurityEvents_AgentId" CHECK ("AgentId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentSecurityEvents_CredentialId" CHECK ("CredentialId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentSecurityEvents_Facts" CHECK ("Kind" IN ('GrantIssued','ClaimBound','PossessionProved','Activated','RotationIssued','Rotated','Revoked','ProviderChanged','SessionAcquired','ConflictingIncarnation','RecoveryRequired') AND "AuthorizationVersion" > 0 AND "Fence" > 0 AND "PreviousAuthorizationVersion" > 0 AND "PreviousFence" > 0 AND "PreviousAuthorizationVersion" <= "AuthorizationVersion" AND "PreviousFence" <= "Fence" AND num_nonnulls("Issuer","Subject") IN (0,2)),
    CONSTRAINT "CK_AgentSecurityEvents_GrantId" CHECK ("GrantId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentSecurityEvents_Id" CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "FK_AgentSecurityEvents_AgentCredentials_AgentId_CredentialId" FOREIGN KEY ("AgentId", "CredentialId") REFERENCES "AgentCredentials" ("AgentId", "Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentSecurityEvents_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "AgentEnrollmentGrants" (
    "Id" uuid NOT NULL,
    "AgentId" uuid NOT NULL,
    "ProviderInstanceId" uuid NOT NULL,
    "Kind" character varying(32) NOT NULL,
    "State" character varying(32) NOT NULL,
    "Version" bigint NOT NULL,
    "ExpectedAgentVersion" bigint NOT NULL,
    "ExpectedAuthorizationVersion" bigint NOT NULL,
    "ExpectedAssignmentVersion" bigint NOT NULL,
    "ProviderVersion" bigint NOT NULL,
    "ProviderCredentialVersion" bigint,
    "Issuer" character varying(2048) COLLATE "C",
    "Subject" character varying(512) COLLATE "C",
    "ActorDigest" bytea,
    "ClientRequestId" uuid NOT NULL,
    "RequestDigest" bytea NOT NULL,
    "TokenHash" bytea,
    "InitiatingCredentialId" uuid,
    "ExpectedActiveCredentialId" uuid,
    "ClaimId" uuid,
    "ClaimDigest" bytea,
    "ClaimedCredentialId" uuid,
    "Nonce" bytea,
    "ClaimReceiptVersion" bigint,
    "ProofRequestId" uuid,
    "ProofReceiptVersion" bigint,
    "RedemptionReceiptVersion" bigint,
    "ActivatedAuthorizationVersion" bigint,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "ClaimedAtUtc" timestamp with time zone,
    "ProvedAtUtc" timestamp with time zone,
    "RedeemedAtUtc" timestamp with time zone,
    CONSTRAINT "PK_AgentEnrollmentGrants" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_AgentEnrollmentGrants_AgentId" CHECK ("AgentId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentEnrollmentGrants_Claim" CHECK (num_nonnulls("ClaimId","ClaimDigest","ClaimedCredentialId","Nonce","ClaimReceiptVersion","ClaimedAtUtc") IN (0,6) AND ("ClaimId" IS NULL OR (octet_length("ClaimDigest") = 32 AND octet_length("Nonce") = 32 AND "ClaimReceiptVersion" > 0 AND "ClaimedAtUtc" >= "CreatedAtUtc" AND "ClaimedAtUtc" < "ExpiresAtUtc")) AND ("State" NOT IN ('Claimed','Proven','Redeemed') OR "ClaimId" IS NOT NULL)),
    CONSTRAINT "CK_AgentEnrollmentGrants_ClaimedCredentialId" CHECK ("ClaimedCredentialId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentEnrollmentGrants_ClaimId" CHECK ("ClaimId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentEnrollmentGrants_ClientRequestId" CHECK ("ClientRequestId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentEnrollmentGrants_Digests" CHECK (octet_length("RequestDigest") = 32 AND ("TokenHash" IS NULL OR octet_length("TokenHash") = 32)),
    CONSTRAINT "CK_AgentEnrollmentGrants_ExpectedActiveCredentialId" CHECK ("ExpectedActiveCredentialId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentEnrollmentGrants_Id" CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentEnrollmentGrants_InitiatingCredentialId" CHECK ("InitiatingCredentialId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentEnrollmentGrants_Kind" CHECK (("Kind" = 'Enrollment' AND "Issuer" IS NOT NULL AND length("Issuer") > 0 AND "Subject" IS NOT NULL AND length("Subject") > 0 AND "ActorDigest" IS NOT NULL AND octet_length("ActorDigest") = 32 AND "InitiatingCredentialId" IS NULL AND "ExpectedActiveCredentialId" IS NULL AND ("State" <> 'Issued' OR "TokenHash" IS NOT NULL)) OR ("Kind" = 'Rotation' AND "Issuer" IS NULL AND "Subject" IS NULL AND "ActorDigest" IS NULL AND "TokenHash" IS NULL AND "InitiatingCredentialId" IS NOT NULL AND "ExpectedActiveCredentialId" IS NOT NULL AND "InitiatingCredentialId" = "ExpectedActiveCredentialId")),
    CONSTRAINT "CK_AgentEnrollmentGrants_Lifetime" CHECK ("ExpiresAtUtc" > "CreatedAtUtc" AND "ExpiresAtUtc" <= "CreatedAtUtc" + interval '10 minutes'),
    CONSTRAINT "CK_AgentEnrollmentGrants_Proof" CHECK (num_nonnulls("ProofRequestId","ProofReceiptVersion","ProvedAtUtc") IN (0,3) AND ("ProofRequestId" IS NULL OR ("ClaimId" IS NOT NULL AND "ProofReceiptVersion" > "ClaimReceiptVersion" AND "ProvedAtUtc" >= "ClaimedAtUtc" AND "ProvedAtUtc" < "ExpiresAtUtc")) AND ("State" NOT IN ('Proven','Redeemed') OR "ProofRequestId" IS NOT NULL)),
    CONSTRAINT "CK_AgentEnrollmentGrants_ProofRequestId" CHECK ("ProofRequestId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentEnrollmentGrants_ProviderInstanceId" CHECK ("ProviderInstanceId" <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT "CK_AgentEnrollmentGrants_Redemption" CHECK (num_nonnulls("RedemptionReceiptVersion","ActivatedAuthorizationVersion","RedeemedAtUtc") IN (0,3) AND (("State" = 'Redeemed') = ("RedeemedAtUtc" IS NOT NULL)) AND ("RedeemedAtUtc" IS NULL OR ("ProofRequestId" IS NOT NULL AND "RedemptionReceiptVersion" > "ProofReceiptVersion" AND "ActivatedAuthorizationVersion" > "ExpectedAuthorizationVersion" AND "RedeemedAtUtc" >= "ProvedAtUtc" AND "RedeemedAtUtc" < "ExpiresAtUtc"))),
    CONSTRAINT "CK_AgentEnrollmentGrants_State" CHECK ("State" IN ('Issued','Claimed','Proven','Redeemed','Expired','Cancelled') AND "Kind" IN ('Enrollment','Rotation')),
    CONSTRAINT "CK_AgentEnrollmentGrants_Versions" CHECK ("Version" > 0 AND "ExpectedAgentVersion" > 0 AND "ExpectedAuthorizationVersion" > 0 AND "ExpectedAssignmentVersion" > 0 AND "ProviderVersion" > 0 AND ("ProviderCredentialVersion" IS NULL OR "ProviderCredentialVersion" > 0)),
    CONSTRAINT "FK_AgentEnrollmentGrants_AgentCredentials_AgentId_ClaimedCrede~" FOREIGN KEY ("AgentId", "ClaimedCredentialId") REFERENCES "AgentCredentials" ("AgentId", "Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentEnrollmentGrants_AgentCredentials_AgentId_ExpectedActi~" FOREIGN KEY ("AgentId", "ExpectedActiveCredentialId") REFERENCES "AgentCredentials" ("AgentId", "Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentEnrollmentGrants_AgentCredentials_AgentId_InitiatingCr~" FOREIGN KEY ("AgentId", "InitiatingCredentialId") REFERENCES "AgentCredentials" ("AgentId", "Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentEnrollmentGrants_AgentProviderAssignments_ProviderInst~" FOREIGN KEY ("ProviderInstanceId", "AgentId") REFERENCES "AgentProviderAssignments" ("ProviderInstanceId", "AgentId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AgentEnrollmentGrants_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_AgentCredentials_AgentId_State" ON "AgentCredentials" ("AgentId", "State");

CREATE UNIQUE INDEX "UX_AgentCredential_Active" ON "AgentCredentials" ("AgentId") WHERE "State" = 'Active';

CREATE UNIQUE INDEX "UX_AgentCredential_Der" ON "AgentCredentials" ("DerSha256");

CREATE INDEX "IX_AgentEnrollmentGrants_AgentId_ClaimedCredentialId" ON "AgentEnrollmentGrants" ("AgentId", "ClaimedCredentialId");

CREATE INDEX "IX_AgentEnrollmentGrants_AgentId_ExpectedActiveCredentialId" ON "AgentEnrollmentGrants" ("AgentId", "ExpectedActiveCredentialId");

CREATE INDEX "IX_AgentEnrollmentGrants_AgentId_InitiatingCredentialId" ON "AgentEnrollmentGrants" ("AgentId", "InitiatingCredentialId");

CREATE UNIQUE INDEX "IX_AgentEnrollmentGrants_ClaimId" ON "AgentEnrollmentGrants" ("ClaimId") WHERE "ClaimId" IS NOT NULL;

CREATE UNIQUE INDEX "IX_AgentEnrollmentGrants_ProofRequestId" ON "AgentEnrollmentGrants" ("ProofRequestId") WHERE "ProofRequestId" IS NOT NULL;

CREATE INDEX "IX_AgentEnrollmentGrants_ProviderInstanceId_AgentId" ON "AgentEnrollmentGrants" ("ProviderInstanceId", "AgentId");

CREATE INDEX "IX_AgentEnrollmentGrants_State_ExpiresAtUtc_Id" ON "AgentEnrollmentGrants" ("State", "ExpiresAtUtc", "Id");

CREATE UNIQUE INDEX "UX_AgentGrant_ActorRequest" ON "AgentEnrollmentGrants" ("ActorDigest", "ClientRequestId") WHERE "Kind" = 'Enrollment';

CREATE UNIQUE INDEX "UX_AgentGrant_LiveAgent" ON "AgentEnrollmentGrants" ("AgentId") WHERE "State" IN ('Issued','Claimed','Proven');

CREATE UNIQUE INDEX "UX_AgentGrant_LiveProvider" ON "AgentEnrollmentGrants" ("ProviderInstanceId") WHERE "Kind" = 'Enrollment' AND "State" IN ('Issued','Claimed','Proven');

CREATE UNIQUE INDEX "UX_AgentGrant_RotationRequest" ON "AgentEnrollmentGrants" ("InitiatingCredentialId", "ClientRequestId") WHERE "Kind" = 'Rotation';

CREATE INDEX "IX_AgentProviderAssignments_AgentId_State" ON "AgentProviderAssignments" ("AgentId", "State");

CREATE INDEX "IX_AgentSecurityEvents_AgentId_CredentialId" ON "AgentSecurityEvents" ("AgentId", "CredentialId");

CREATE INDEX "IX_AgentSecurityEvents_AgentId_RecordedAtUtc_Id" ON "AgentSecurityEvents" ("AgentId", "RecordedAtUtc", "Id");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261111081939_AgentControlFoundation', '10.0.13');

COMMIT;
