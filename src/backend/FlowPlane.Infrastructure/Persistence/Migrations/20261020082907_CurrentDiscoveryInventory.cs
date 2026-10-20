using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowPlane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CurrentDiscoveryInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderDiscoveryGenerations",
                columns: table => new
                {
                    RefreshId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseGenerationId = table.Column<Guid>(type: "uuid", nullable: true),
                    FenceToken = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedPublicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    ProviderVersion = table.Column<long>(type: "bigint", nullable: false),
                    ProviderType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CredentialKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ManagedSecretVersion = table.Column<long>(type: "bigint", nullable: true),
                    AccessContextId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ScanStartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ScanFinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ScanCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    ScanOutcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SourceCompatibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ObservationCount = table.Column<long>(type: "bigint", nullable: false),
                    CandidateCount = table.Column<long>(type: "bigint", nullable: false),
                    IncompatibleCount = table.Column<long>(type: "bigint", nullable: false),
                    AddedCount = table.Column<long>(type: "bigint", nullable: false),
                    ChangedCount = table.Column<long>(type: "bigint", nullable: false),
                    UnchangedCount = table.Column<long>(type: "bigint", nullable: false),
                    NotObservedCount = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderDiscoveryGenerations", x => x.RefreshId);
                    table.UniqueConstraint("AK_ProviderDiscoveryGenerations_ProviderInstanceId_RefreshId", x => new { x.ProviderInstanceId, x.RefreshId });
                    table.CheckConstraint("CK_DiscoveryGeneration_Counts", "\"ObservationCount\" >= 0 AND \"CandidateCount\" >= 0 AND \"CandidateCount\" <= \"ObservationCount\" AND \"IncompatibleCount\" >= 0 AND \"IncompatibleCount\" <= \"ObservationCount\" AND \"AddedCount\" >= 0 AND \"ChangedCount\" >= 0 AND \"UnchangedCount\" >= 0 AND \"NotObservedCount\" >= 0");
                    table.CheckConstraint("CK_DiscoveryGeneration_Credential", "(\"CredentialKind\" = 'FlowPlane' AND \"ManagedSecretVersion\" IS NOT NULL) OR (\"CredentialKind\" = 'Environment' AND \"ManagedSecretVersion\" IS NULL)");
                    table.CheckConstraint("CK_DiscoveryGeneration_Expiry", "\"ExpiresAtUtc\" > \"StartedAtUtc\"");
                    table.CheckConstraint("CK_DiscoveryGeneration_Publication", "\"Status\" <> 'Published' OR (\"PublishedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NOT NULL AND \"ScanFinishedAtUtc\" IS NOT NULL AND \"ScanCompleted\" AND \"SourceCompatibility\" IS NOT NULL AND \"SourceCompatibility\" = 'Compatible' AND \"ScanOutcome\" IS NOT NULL AND ((\"ScanOutcome\" = 'Succeeded' AND \"IncompatibleCount\" = 0) OR (\"ScanOutcome\" = 'Partial' AND \"IncompatibleCount\" > 0)))");
                    table.CheckConstraint("CK_DiscoveryGeneration_Status", "\"Status\" IN ('Staging','Preparing','Ready','Published','Failed','Cancelled','Superseded','Abandoned')");
                    table.CheckConstraint("CK_DiscoveryGeneration_Versions", "\"Version\" > 0 AND \"ProviderVersion\" > 0 AND \"FenceToken\" > 0 AND \"ExpectedPublicationVersion\" >= 0 AND (\"ManagedSecretVersion\" IS NULL OR \"ManagedSecretVersion\" > 0)");
                    table.ForeignKey(
                        name: "FK_ProviderDiscoveryGenerations_ProviderInstances_ProviderInst~",
                        column: x => x.ProviderInstanceId,
                        principalTable: "ProviderInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProviderDiscoveryObservations",
                columns: table => new
                {
                    ProviderInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RefreshId = table.Column<Guid>(type: "uuid", nullable: false),
                    NativeKeyDigest = table.Column<byte[]>(type: "bytea", nullable: false),
                    NativeKey = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    SchemaId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    CandidateSchemaId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CandidateJson = table.Column<string>(type: "jsonb", nullable: true),
                    ContentFingerprint = table.Column<byte[]>(type: "bytea", nullable: true),
                    LastObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ObservedAccessContextId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservedProviderVersion = table.Column<long>(type: "bigint", nullable: false),
                    ObservedSecretVersion = table.Column<long>(type: "bigint", nullable: true),
                    NotObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderDiscoveryObservations", x => new { x.ProviderInstanceId, x.RefreshId, x.NativeKeyDigest });
                    table.CheckConstraint("CK_DiscoveryObservation_Digests", "octet_length(\"NativeKeyDigest\") = 32 AND (\"ContentFingerprint\" IS NULL OR octet_length(\"ContentFingerprint\") = 32)");
                    table.CheckConstraint("CK_DiscoveryObservation_Identity", "length(\"NativeKey\") > 0 AND length(\"SchemaId\") > 0 AND \"ObservedProviderVersion\" > 0 AND (\"ObservedSecretVersion\" IS NULL OR \"ObservedSecretVersion\" > 0)");
                    table.CheckConstraint("CK_DiscoveryObservation_Payload", "(\"Visibility\" = 'Observed' AND \"MetadataJson\" IS NOT NULL AND \"ContentFingerprint\" IS NOT NULL AND \"NotObservedAtUtc\" IS NULL AND ((\"CandidateSchemaId\" IS NULL AND \"CandidateJson\" IS NULL) OR (\"CandidateSchemaId\" IS NOT NULL AND \"CandidateJson\" IS NOT NULL))) OR (\"Visibility\" = 'NotObserved' AND \"MetadataJson\" IS NULL AND \"ContentFingerprint\" IS NULL AND \"CandidateSchemaId\" IS NULL AND \"CandidateJson\" IS NULL AND \"NotObservedAtUtc\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ProviderDiscoveryObservations_ProviderDiscoveryGenerations_~",
                        columns: x => new { x.ProviderInstanceId, x.RefreshId },
                        principalTable: "ProviderDiscoveryGenerations",
                        principalColumns: new[] { "ProviderInstanceId", "RefreshId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProviderDiscoveryStates",
                columns: table => new
                {
                    ProviderInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentGenerationId = table.Column<Guid>(type: "uuid", nullable: true),
                    InFlightGenerationId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastAttemptGenerationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PublicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    FenceToken = table.Column<long>(type: "bigint", nullable: false),
                    Lifecycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderDiscoveryStates", x => x.ProviderInstanceId);
                    table.CheckConstraint("CK_DiscoveryState_Counters", "\"PublicationVersion\" >= 0 AND \"FenceToken\" >= 0");
                    table.CheckConstraint("CK_DiscoveryState_Lifecycle", "\"Lifecycle\" IN ('Active', 'Deleting')");
                    table.CheckConstraint("CK_DiscoveryState_Pointers", "\"CurrentGenerationId\" IS NULL OR \"InFlightGenerationId\" IS NULL OR \"CurrentGenerationId\" <> \"InFlightGenerationId\"");
                    table.ForeignKey(
                        name: "FK_ProviderDiscoveryStates_ProviderDiscoveryGenerations_Provid~",
                        columns: x => new { x.ProviderInstanceId, x.CurrentGenerationId },
                        principalTable: "ProviderDiscoveryGenerations",
                        principalColumns: new[] { "ProviderInstanceId", "RefreshId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderDiscoveryStates_ProviderDiscoveryGenerations_Provi~1",
                        columns: x => new { x.ProviderInstanceId, x.InFlightGenerationId },
                        principalTable: "ProviderDiscoveryGenerations",
                        principalColumns: new[] { "ProviderInstanceId", "RefreshId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderDiscoveryStates_ProviderDiscoveryGenerations_Provi~2",
                        columns: x => new { x.ProviderInstanceId, x.LastAttemptGenerationId },
                        principalTable: "ProviderDiscoveryGenerations",
                        principalColumns: new[] { "ProviderInstanceId", "RefreshId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderDiscoveryStates_ProviderInstances_ProviderInstanceId",
                        column: x => x.ProviderInstanceId,
                        principalTable: "ProviderInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderDiscoveryGenerations_ProviderInstanceId_FinishedAtU~",
                table: "ProviderDiscoveryGenerations",
                columns: new[] { "ProviderInstanceId", "FinishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderDiscoveryGenerations_Status_ExpiresAtUtc",
                table: "ProviderDiscoveryGenerations",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_DiscoveryGeneration_ActiveClaim",
                table: "ProviderDiscoveryGenerations",
                column: "ProviderInstanceId",
                unique: true,
                filter: "\"Status\" IN ('Staging', 'Preparing', 'Ready')");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderDiscoveryObservations_ProviderInstanceId_RefreshId_~",
                table: "ProviderDiscoveryObservations",
                columns: new[] { "ProviderInstanceId", "RefreshId", "CandidateSchemaId" },
                filter: "\"Visibility\" = 'Observed' AND \"CandidateSchemaId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderDiscoveryObservations_ProviderInstanceId_RefreshId~1",
                table: "ProviderDiscoveryObservations",
                columns: new[] { "ProviderInstanceId", "RefreshId", "Visibility" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderDiscoveryStates_ProviderInstanceId_CurrentGeneratio~",
                table: "ProviderDiscoveryStates",
                columns: new[] { "ProviderInstanceId", "CurrentGenerationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderDiscoveryStates_ProviderInstanceId_InFlightGenerati~",
                table: "ProviderDiscoveryStates",
                columns: new[] { "ProviderInstanceId", "InFlightGenerationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderDiscoveryStates_ProviderInstanceId_LastAttemptGener~",
                table: "ProviderDiscoveryStates",
                columns: new[] { "ProviderInstanceId", "LastAttemptGenerationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderDiscoveryObservations");

            migrationBuilder.DropTable(
                name: "ProviderDiscoveryStates");

            migrationBuilder.DropTable(
                name: "ProviderDiscoveryGenerations");
        }
    }
}
