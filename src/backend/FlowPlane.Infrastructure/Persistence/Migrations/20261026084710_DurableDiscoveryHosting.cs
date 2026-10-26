using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowPlane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableDiscoveryHosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_DiscoveryGeneration_ActiveClaim",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DiscoveryGeneration_Status",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.AddColumn<int>(
                name: "ExecutionSlot",
                table: "ProviderDiscoveryGenerations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutorId",
                table: "ProviderDiscoveryGenerations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAtUtc",
                table: "ProviderDiscoveryGenerations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublicationAttemptedAtUtc",
                table: "ProviderDiscoveryGenerations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PublicationUncertain",
                table: "ProviderDiscoveryGenerations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReceiptExpiresAtUtc",
                table: "ProviderDiscoveryGenerations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestingIssuer",
                table: "ProviderDiscoveryGenerations",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<string>(
                name: "RequestingSubject",
                table: "ProviderDiscoveryGenerations",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true,
                collation: "C");

            migrationBuilder.CreateIndex(
                name: "UX_DiscoveryGeneration_ActiveClaim",
                table: "ProviderDiscoveryGenerations",
                column: "ProviderInstanceId",
                unique: true,
                filter: "\"Status\" IN ('Accepted', 'Staging', 'Preparing', 'Ready')");

            migrationBuilder.CreateIndex(
                name: "UX_DiscoveryGeneration_HostedSlot",
                table: "ProviderDiscoveryGenerations",
                column: "ExecutionSlot",
                unique: true,
                filter: "\"ExecutionSlot\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DiscoveryGeneration_Hosting",
                table: "ProviderDiscoveryGenerations",
                sql: "(\"RequestingIssuer\" IS NULL AND \"RequestingSubject\" IS NULL AND \"ExecutionSlot\" IS NULL AND \"ExecutorId\" IS NULL AND \"LeaseExpiresAtUtc\" IS NULL AND \"ReceiptExpiresAtUtc\" IS NULL AND \"PublicationAttemptedAtUtc\" IS NULL AND NOT \"PublicationUncertain\" AND \"Status\" <> 'Accepted') OR (\"RequestingIssuer\" IS NOT NULL AND length(\"RequestingIssuer\") > 0 AND \"RequestingSubject\" IS NOT NULL AND length(\"RequestingSubject\") > 0 AND \"ReceiptExpiresAtUtc\" IS NOT NULL AND \"ReceiptExpiresAtUtc\" > \"ExpiresAtUtc\" AND (\"ExecutionSlot\" IS NULL OR \"ExecutionSlot\" = 1) AND (\"ExecutionSlot\" IS NULL OR \"LeaseExpiresAtUtc\" IS NOT NULL) AND (\"Status\" <> 'Accepted' OR (\"ExecutionSlot\" = 1 AND \"ExecutorId\" IS NULL)) AND (\"Status\" NOT IN ('Staging','Preparing','Ready') OR (\"ExecutionSlot\" = 1 AND \"ExecutorId\" IS NOT NULL AND \"LeaseExpiresAtUtc\" IS NOT NULL)))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DiscoveryGeneration_HostingPresence",
                table: "ProviderDiscoveryGenerations",
                sql: "\"RequestingIssuer\" IS NULL OR ((\"Status\" NOT IN ('Accepted','Staging','Preparing','Ready') OR \"ExecutionSlot\" IS NOT NULL) AND (NOT \"PublicationUncertain\" OR \"PublicationAttemptedAtUtc\" IS NOT NULL) AND substring(\"RefreshId\"::text, 15, 1) = '7' AND substring(\"RefreshId\"::text, 20, 1) IN ('8','9','a','b'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DiscoveryGeneration_Status",
                table: "ProviderDiscoveryGenerations",
                sql: "\"Status\" IN ('Accepted','Staging','Preparing','Ready','Published','Failed','Cancelled','Superseded','Abandoned')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $rollback$
                BEGIN
                  LOCK TABLE "ProviderDiscoveryGenerations" IN ACCESS EXCLUSIVE MODE;
                  IF EXISTS (SELECT 1 FROM "ProviderDiscoveryGenerations" WHERE "ExecutionSlot" IS NOT NULL OR "Status" = 'Accepted' OR "PublicationUncertain") THEN
                    RAISE EXCEPTION 'Quiesce and reconcile hosted discovery before Gate B rollback';
                  END IF;
                END $rollback$;
                """);

            migrationBuilder.DropIndex(
                name: "UX_DiscoveryGeneration_ActiveClaim",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.DropIndex(
                name: "UX_DiscoveryGeneration_HostedSlot",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DiscoveryGeneration_Hosting",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DiscoveryGeneration_HostingPresence",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DiscoveryGeneration_Status",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.DropColumn(
                name: "ExecutionSlot",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.DropColumn(
                name: "ExecutorId",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtc",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.DropColumn(
                name: "PublicationAttemptedAtUtc",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.DropColumn(
                name: "PublicationUncertain",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.DropColumn(
                name: "ReceiptExpiresAtUtc",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.DropColumn(
                name: "RequestingIssuer",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.DropColumn(
                name: "RequestingSubject",
                table: "ProviderDiscoveryGenerations");

            migrationBuilder.CreateIndex(
                name: "UX_DiscoveryGeneration_ActiveClaim",
                table: "ProviderDiscoveryGenerations",
                column: "ProviderInstanceId",
                unique: true,
                filter: "\"Status\" IN ('Staging', 'Preparing', 'Ready')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DiscoveryGeneration_Status",
                table: "ProviderDiscoveryGenerations",
                sql: "\"Status\" IN ('Staging','Preparing','Ready','Published','Failed','Cancelled','Superseded','Abandoned')");
        }
    }
}
