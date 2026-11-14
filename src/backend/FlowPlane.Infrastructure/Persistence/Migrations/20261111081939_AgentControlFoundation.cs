using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowPlane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentControlFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    AuthorizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    SessionFence = table.Column<long>(type: "bigint", nullable: false),
                    CurrentBootId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectDigest = table.Column<byte[]>(type: "bytea", nullable: true),
                    ConnectAcceptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConnectLeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConnectAuthorizationVersion = table.Column<long>(type: "bigint", nullable: true),
                    ConnectFence = table.Column<long>(type: "bigint", nullable: true),
                    LeaseAuthorizationVersion = table.Column<long>(type: "bigint", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PollRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    PollDigest = table.Column<byte[]>(type: "bytea", nullable: true),
                    PollSequence = table.Column<long>(type: "bigint", nullable: false),
                    PollAcknowledgedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PollLeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PollFence = table.Column<long>(type: "bigint", nullable: true),
                    PollAuthorizationVersion = table.Column<long>(type: "bigint", nullable: true),
                    NextPollSeconds = table.Column<int>(type: "integer", nullable: true),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: true),
                    PackageVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Readiness = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RecoveryRequired = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastContactAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastHeartbeatAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByIssuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true, collation: "C"),
                    RevokedBySubject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.CheckConstraint("CK_Agents_ConnectRequestId", "\"ConnectRequestId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_Agents_CurrentBootId", "\"CurrentBootId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_Agents_Display", "length(\"DisplayName\") BETWEEN 1 AND 200");
                    table.CheckConstraint("CK_Agents_Id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_Agents_Lifetime", "\"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_Agents_Package", "num_nonnulls(\"ProtocolVersion\",\"PackageVersion\") IN (0,2) AND (\"ProtocolVersion\" IS NULL OR (\"ProtocolVersion\" = 1 AND length(\"PackageVersion\") BETWEEN 1 AND 64))");
                    table.CheckConstraint("CK_Agents_Poll", "num_nonnulls(\"PollRequestId\",\"PollDigest\",\"PollAcknowledgedAtUtc\",\"PollLeaseExpiresAtUtc\",\"PollFence\",\"PollAuthorizationVersion\",\"NextPollSeconds\") IN (0,7) AND ((\"PollSequence\" = 0 AND \"PollRequestId\" IS NULL) OR (\"PollSequence\" > 0 AND \"PollRequestId\" IS NOT NULL AND \"SessionId\" IS NOT NULL AND octet_length(\"PollDigest\") = 32 AND \"PollFence\" = \"ConnectFence\" AND \"PollAuthorizationVersion\" = \"ConnectAuthorizationVersion\" AND \"NextPollSeconds\" = 5 AND \"PollLeaseExpiresAtUtc\" > \"PollAcknowledgedAtUtc\"))");
                    table.CheckConstraint("CK_Agents_PollRequestId", "\"PollRequestId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_Agents_Revocation", "num_nonnulls(\"RevokedAtUtc\",\"RevokedByIssuer\",\"RevokedBySubject\") IN (0,3) AND ((\"State\" = 'Revoked') = (\"RevokedAtUtc\" IS NOT NULL))");
                    table.CheckConstraint("CK_Agents_Session", "num_nonnulls(\"CurrentBootId\",\"ConnectRequestId\",\"SessionId\",\"ConnectDigest\",\"ConnectAcceptedAtUtc\",\"ConnectLeaseExpiresAtUtc\",\"ConnectAuthorizationVersion\",\"ConnectFence\",\"LeaseAuthorizationVersion\",\"LeaseExpiresAtUtc\") IN (0,10) AND (\"SessionId\" IS NULL OR (octet_length(\"ConnectDigest\") = 32 AND \"ConnectFence\" > 0 AND \"ConnectFence\" <= \"SessionFence\" AND \"ConnectAuthorizationVersion\" > 0 AND \"LeaseAuthorizationVersion\" = \"ConnectAuthorizationVersion\" AND \"ConnectLeaseExpiresAtUtc\" > \"ConnectAcceptedAtUtc\" AND \"LeaseExpiresAtUtc\" >= \"ConnectLeaseExpiresAtUtc\" AND \"ProtocolVersion\" IS NOT NULL))");
                    table.CheckConstraint("CK_Agents_SessionId", "\"SessionId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_Agents_States", "\"State\" IN ('Pending','Active','Quarantined','Revoked') AND \"Readiness\" IN ('Unknown','ReadyForControlPlane','Unavailable','ActionRequired')");
                    table.CheckConstraint("CK_Agents_Versions", "\"Version\" > 0 AND \"AuthorizationVersion\" > 0 AND \"SessionFence\" > 0 AND \"PollSequence\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "AgentCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Der = table.Column<byte[]>(type: "bytea", nullable: false),
                    DerSha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    SpkiSha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    NotBeforeUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NotAfterUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActivatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetiringUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCredentials", x => x.Id);
                    table.UniqueConstraint("AK_AgentCredentials_AgentId_Id", x => new { x.AgentId, x.Id });
                    table.CheckConstraint("CK_AgentCredentials_AgentId", "\"AgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentCredentials_Der", "octet_length(\"Der\") BETWEEN 1 AND 16384");
                    table.CheckConstraint("CK_AgentCredentials_Digests", "octet_length(\"DerSha256\") = 32 AND octet_length(\"SpkiSha256\") = 32");
                    table.CheckConstraint("CK_AgentCredentials_Id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentCredentials_Lifecycle", "(\"State\" NOT IN ('Active','RetiringReceiptsOnly','Retired') OR (\"ProvedAtUtc\" IS NOT NULL AND \"ActivatedAtUtc\" IS NOT NULL AND \"ActivatedAtUtc\" >= \"ProvedAtUtc\")) AND (\"State\" <> 'RetiringReceiptsOnly' OR (\"RetiringUntilUtc\" IS NOT NULL AND \"RetiringUntilUtc\" > \"ActivatedAtUtc\")) AND ((\"State\" = 'Revoked') = (\"RevokedAtUtc\" IS NOT NULL))");
                    table.CheckConstraint("CK_AgentCredentials_State", "\"State\" IN ('PendingProof','Active','RetiringReceiptsOnly','Retired','Revoked') AND \"Version\" > 0");
                    table.CheckConstraint("CK_AgentCredentials_Validity", "\"NotAfterUtc\" > \"NotBeforeUtc\"");
                    table.ForeignKey(
                        name: "FK_AgentCredentials_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentProviderAssignments",
                columns: table => new
                {
                    ProviderInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ProviderVersion = table.Column<long>(type: "bigint", nullable: false),
                    ProviderCredentialVersion = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentProviderAssignments", x => x.ProviderInstanceId);
                    table.UniqueConstraint("AK_AgentProviderAssignments_ProviderInstanceId_AgentId", x => new { x.ProviderInstanceId, x.AgentId });
                    table.CheckConstraint("CK_AgentProviderAssignments_AgentId", "\"AgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentProviderAssignments_ProviderInstanceId", "\"ProviderInstanceId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentProviderAssignments_State", "\"State\" IN ('Pending','Active','Suspended','Revoked')");
                    table.CheckConstraint("CK_AgentProviderAssignments_Versions", "\"Version\" > 0 AND \"ProviderVersion\" > 0 AND (\"ProviderCredentialVersion\" IS NULL OR \"ProviderCredentialVersion\" > 0) AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_AgentProviderAssignments_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentProviderAssignments_ProviderInstances_ProviderInstance~",
                        column: x => x.ProviderInstanceId,
                        principalTable: "ProviderInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentSecurityEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true, collation: "C"),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true, collation: "C"),
                    AuthorizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    Fence = table.Column<long>(type: "bigint", nullable: false),
                    PreviousAuthorizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    PreviousFence = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSecurityEvents", x => x.Id);
                    table.CheckConstraint("CK_AgentSecurityEvents_AgentId", "\"AgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentSecurityEvents_CredentialId", "\"CredentialId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentSecurityEvents_Facts", "\"Kind\" IN ('GrantIssued','ClaimBound','PossessionProved','Activated','RotationIssued','Rotated','Revoked','ProviderChanged','SessionAcquired','ConflictingIncarnation','RecoveryRequired') AND \"AuthorizationVersion\" > 0 AND \"Fence\" > 0 AND \"PreviousAuthorizationVersion\" > 0 AND \"PreviousFence\" > 0 AND \"PreviousAuthorizationVersion\" <= \"AuthorizationVersion\" AND \"PreviousFence\" <= \"Fence\" AND num_nonnulls(\"Issuer\",\"Subject\") IN (0,2)");
                    table.CheckConstraint("CK_AgentSecurityEvents_GrantId", "\"GrantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentSecurityEvents_Id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_AgentSecurityEvents_AgentCredentials_AgentId_CredentialId",
                        columns: x => new { x.AgentId, x.CredentialId },
                        principalTable: "AgentCredentials",
                        principalColumns: new[] { "AgentId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentSecurityEvents_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentEnrollmentGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedAgentVersion = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedAuthorizationVersion = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedAssignmentVersion = table.Column<long>(type: "bigint", nullable: false),
                    ProviderVersion = table.Column<long>(type: "bigint", nullable: false),
                    ProviderCredentialVersion = table.Column<long>(type: "bigint", nullable: true),
                    Issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true, collation: "C"),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true, collation: "C"),
                    ActorDigest = table.Column<byte[]>(type: "bytea", nullable: true),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestDigest = table.Column<byte[]>(type: "bytea", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: true),
                    InitiatingCredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpectedActiveCredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimDigest = table.Column<byte[]>(type: "bytea", nullable: true),
                    ClaimedCredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    ClaimReceiptVersion = table.Column<long>(type: "bigint", nullable: true),
                    ProofRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProofReceiptVersion = table.Column<long>(type: "bigint", nullable: true),
                    RedemptionReceiptVersion = table.Column<long>(type: "bigint", nullable: true),
                    ActivatedAuthorizationVersion = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClaimedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RedeemedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEnrollmentGrants", x => x.Id);
                    table.CheckConstraint("CK_AgentEnrollmentGrants_AgentId", "\"AgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_Claim", "num_nonnulls(\"ClaimId\",\"ClaimDigest\",\"ClaimedCredentialId\",\"Nonce\",\"ClaimReceiptVersion\",\"ClaimedAtUtc\") IN (0,6) AND (\"ClaimId\" IS NULL OR (octet_length(\"ClaimDigest\") = 32 AND octet_length(\"Nonce\") = 32 AND \"ClaimReceiptVersion\" > 0 AND \"ClaimedAtUtc\" >= \"CreatedAtUtc\" AND \"ClaimedAtUtc\" < \"ExpiresAtUtc\")) AND (\"State\" NOT IN ('Claimed','Proven','Redeemed') OR \"ClaimId\" IS NOT NULL)");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_ClaimedCredentialId", "\"ClaimedCredentialId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_ClaimId", "\"ClaimId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_ClientRequestId", "\"ClientRequestId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_Digests", "octet_length(\"RequestDigest\") = 32 AND (\"TokenHash\" IS NULL OR octet_length(\"TokenHash\") = 32)");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_ExpectedActiveCredentialId", "\"ExpectedActiveCredentialId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_Id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_InitiatingCredentialId", "\"InitiatingCredentialId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_Kind", "(\"Kind\" = 'Enrollment' AND \"Issuer\" IS NOT NULL AND length(\"Issuer\") > 0 AND \"Subject\" IS NOT NULL AND length(\"Subject\") > 0 AND \"ActorDigest\" IS NOT NULL AND octet_length(\"ActorDigest\") = 32 AND \"InitiatingCredentialId\" IS NULL AND \"ExpectedActiveCredentialId\" IS NULL AND (\"State\" <> 'Issued' OR \"TokenHash\" IS NOT NULL)) OR (\"Kind\" = 'Rotation' AND \"Issuer\" IS NULL AND \"Subject\" IS NULL AND \"ActorDigest\" IS NULL AND \"TokenHash\" IS NULL AND \"InitiatingCredentialId\" IS NOT NULL AND \"ExpectedActiveCredentialId\" IS NOT NULL AND \"InitiatingCredentialId\" = \"ExpectedActiveCredentialId\")");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_Lifetime", "\"ExpiresAtUtc\" > \"CreatedAtUtc\" AND \"ExpiresAtUtc\" <= \"CreatedAtUtc\" + interval '10 minutes'");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_Proof", "num_nonnulls(\"ProofRequestId\",\"ProofReceiptVersion\",\"ProvedAtUtc\") IN (0,3) AND (\"ProofRequestId\" IS NULL OR (\"ClaimId\" IS NOT NULL AND \"ProofReceiptVersion\" > \"ClaimReceiptVersion\" AND \"ProvedAtUtc\" >= \"ClaimedAtUtc\" AND \"ProvedAtUtc\" < \"ExpiresAtUtc\")) AND (\"State\" NOT IN ('Proven','Redeemed') OR \"ProofRequestId\" IS NOT NULL)");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_ProofRequestId", "\"ProofRequestId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_ProviderInstanceId", "\"ProviderInstanceId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_Redemption", "num_nonnulls(\"RedemptionReceiptVersion\",\"ActivatedAuthorizationVersion\",\"RedeemedAtUtc\") IN (0,3) AND ((\"State\" = 'Redeemed') = (\"RedeemedAtUtc\" IS NOT NULL)) AND (\"RedeemedAtUtc\" IS NULL OR (\"ProofRequestId\" IS NOT NULL AND \"RedemptionReceiptVersion\" > \"ProofReceiptVersion\" AND \"ActivatedAuthorizationVersion\" > \"ExpectedAuthorizationVersion\" AND \"RedeemedAtUtc\" >= \"ProvedAtUtc\" AND \"RedeemedAtUtc\" < \"ExpiresAtUtc\"))");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_State", "\"State\" IN ('Issued','Claimed','Proven','Redeemed','Expired','Cancelled') AND \"Kind\" IN ('Enrollment','Rotation')");
                    table.CheckConstraint("CK_AgentEnrollmentGrants_Versions", "\"Version\" > 0 AND \"ExpectedAgentVersion\" > 0 AND \"ExpectedAuthorizationVersion\" > 0 AND \"ExpectedAssignmentVersion\" > 0 AND \"ProviderVersion\" > 0 AND (\"ProviderCredentialVersion\" IS NULL OR \"ProviderCredentialVersion\" > 0)");
                    table.ForeignKey(
                        name: "FK_AgentEnrollmentGrants_AgentCredentials_AgentId_ClaimedCrede~",
                        columns: x => new { x.AgentId, x.ClaimedCredentialId },
                        principalTable: "AgentCredentials",
                        principalColumns: new[] { "AgentId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentEnrollmentGrants_AgentCredentials_AgentId_ExpectedActi~",
                        columns: x => new { x.AgentId, x.ExpectedActiveCredentialId },
                        principalTable: "AgentCredentials",
                        principalColumns: new[] { "AgentId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentEnrollmentGrants_AgentCredentials_AgentId_InitiatingCr~",
                        columns: x => new { x.AgentId, x.InitiatingCredentialId },
                        principalTable: "AgentCredentials",
                        principalColumns: new[] { "AgentId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentEnrollmentGrants_AgentProviderAssignments_ProviderInst~",
                        columns: x => new { x.ProviderInstanceId, x.AgentId },
                        principalTable: "AgentProviderAssignments",
                        principalColumns: new[] { "ProviderInstanceId", "AgentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentEnrollmentGrants_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCredentials_AgentId_State",
                table: "AgentCredentials",
                columns: new[] { "AgentId", "State" });

            migrationBuilder.CreateIndex(
                name: "UX_AgentCredential_Active",
                table: "AgentCredentials",
                column: "AgentId",
                unique: true,
                filter: "\"State\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "UX_AgentCredential_Der",
                table: "AgentCredentials",
                column: "DerSha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollmentGrants_AgentId_ClaimedCredentialId",
                table: "AgentEnrollmentGrants",
                columns: new[] { "AgentId", "ClaimedCredentialId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollmentGrants_AgentId_ExpectedActiveCredentialId",
                table: "AgentEnrollmentGrants",
                columns: new[] { "AgentId", "ExpectedActiveCredentialId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollmentGrants_AgentId_InitiatingCredentialId",
                table: "AgentEnrollmentGrants",
                columns: new[] { "AgentId", "InitiatingCredentialId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollmentGrants_ClaimId",
                table: "AgentEnrollmentGrants",
                column: "ClaimId",
                unique: true,
                filter: "\"ClaimId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollmentGrants_ProofRequestId",
                table: "AgentEnrollmentGrants",
                column: "ProofRequestId",
                unique: true,
                filter: "\"ProofRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollmentGrants_ProviderInstanceId_AgentId",
                table: "AgentEnrollmentGrants",
                columns: new[] { "ProviderInstanceId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollmentGrants_State_ExpiresAtUtc_Id",
                table: "AgentEnrollmentGrants",
                columns: new[] { "State", "ExpiresAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_AgentGrant_ActorRequest",
                table: "AgentEnrollmentGrants",
                columns: new[] { "ActorDigest", "ClientRequestId" },
                unique: true,
                filter: "\"Kind\" = 'Enrollment'");

            migrationBuilder.CreateIndex(
                name: "UX_AgentGrant_LiveAgent",
                table: "AgentEnrollmentGrants",
                column: "AgentId",
                unique: true,
                filter: "\"State\" IN ('Issued','Claimed','Proven')");

            migrationBuilder.CreateIndex(
                name: "UX_AgentGrant_LiveProvider",
                table: "AgentEnrollmentGrants",
                column: "ProviderInstanceId",
                unique: true,
                filter: "\"Kind\" = 'Enrollment' AND \"State\" IN ('Issued','Claimed','Proven')");

            migrationBuilder.CreateIndex(
                name: "UX_AgentGrant_RotationRequest",
                table: "AgentEnrollmentGrants",
                columns: new[] { "InitiatingCredentialId", "ClientRequestId" },
                unique: true,
                filter: "\"Kind\" = 'Rotation'");

            migrationBuilder.CreateIndex(
                name: "IX_AgentProviderAssignments_AgentId_State",
                table: "AgentProviderAssignments",
                columns: new[] { "AgentId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSecurityEvents_AgentId_CredentialId",
                table: "AgentSecurityEvents",
                columns: new[] { "AgentId", "CredentialId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSecurityEvents_AgentId_RecordedAtUtc_Id",
                table: "AgentSecurityEvents",
                columns: new[] { "AgentId", "RecordedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keep the guard and DROP statements in one EF migration transaction. No lease-based evidence erasure.
            migrationBuilder.Sql("""
                LOCK TABLE "ProviderInstances", "FlowPlaneSecrets", "Agents", "AgentProviderAssignments",
                  "AgentCredentials", "AgentEnrollmentGrants", "AgentSecurityEvents" IN ACCESS EXCLUSIVE MODE;
                DO $agent_rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM "Agents" WHERE "State" <> 'Revoked' OR "RecoveryRequired"
                    OR "LeaseExpiresAtUtc" > clock_timestamp())
                    OR EXISTS (SELECT 1 FROM "AgentCredentials" WHERE "State" IN ('PendingProof','Active','RetiringReceiptsOnly'))
                    OR EXISTS (SELECT 1 FROM "AgentProviderAssignments" WHERE "State" <> 'Revoked')
                    OR EXISTS (SELECT 1 FROM "AgentEnrollmentGrants" WHERE "State" IN ('Issued','Claimed','Proven')
                      OR "ExpiresAtUtc" >= clock_timestamp() - interval '24 hours')
                    OR EXISTS (SELECT 1 FROM "AgentSecurityEvents"
                      WHERE "RecordedAtUtc" >= clock_timestamp() - interval '90 days') THEN
                    RAISE EXCEPTION 'Agent rollback requires quiescent revoked identities and no unresolved or retained lifecycle receipts';
                  END IF;
                END $agent_rollback$;
                """);

            migrationBuilder.DropTable(
                name: "AgentEnrollmentGrants");

            migrationBuilder.DropTable(
                name: "AgentSecurityEvents");

            migrationBuilder.DropTable(
                name: "AgentProviderAssignments");

            migrationBuilder.DropTable(
                name: "AgentCredentials");

            migrationBuilder.DropTable(
                name: "Agents");
        }
    }
}
