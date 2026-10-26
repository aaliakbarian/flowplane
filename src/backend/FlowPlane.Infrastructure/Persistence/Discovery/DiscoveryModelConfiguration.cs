using FlowPlane.Domain.Providers;

using Microsoft.EntityFrameworkCore;

namespace FlowPlane.Infrastructure.Persistence.Discovery;

internal static class DiscoveryModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<DiscoveryState>(builder =>
        {
            builder.ToTable("ProviderDiscoveryStates", table =>
            {
                table.HasCheckConstraint("CK_DiscoveryState_Counters", "\"PublicationVersion\" >= 0 AND \"FenceToken\" >= 0");
                table.HasCheckConstraint("CK_DiscoveryState_Lifecycle", "\"Lifecycle\" IN ('Active', 'Deleting')");
                table.HasCheckConstraint("CK_DiscoveryState_Pointers", "\"CurrentGenerationId\" IS NULL OR \"InFlightGenerationId\" IS NULL OR \"CurrentGenerationId\" <> \"InFlightGenerationId\"");
            });
            builder.HasKey(row => row.ProviderInstanceId);
            builder.Property(row => row.Lifecycle).HasConversion<string>().HasMaxLength(16);
            builder.Property(row => row.PublicationVersion).IsConcurrencyToken();
            builder.Property(row => row.FenceToken).IsConcurrencyToken();
            builder.HasOne<ProviderInstance>().WithMany().HasForeignKey(row => row.ProviderInstanceId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<DiscoveryGeneration>().WithMany().HasForeignKey(row => new { row.ProviderInstanceId, row.CurrentGenerationId })
                .HasPrincipalKey(row => new { row.ProviderInstanceId, row.RefreshId }).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<DiscoveryGeneration>().WithMany().HasForeignKey(row => new { row.ProviderInstanceId, row.InFlightGenerationId })
                .HasPrincipalKey(row => new { row.ProviderInstanceId, row.RefreshId }).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<DiscoveryGeneration>().WithMany().HasForeignKey(row => new { row.ProviderInstanceId, row.LastAttemptGenerationId })
                .HasPrincipalKey(row => new { row.ProviderInstanceId, row.RefreshId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<DiscoveryGeneration>(builder =>
        {
            builder.ToTable("ProviderDiscoveryGenerations", table =>
            {
                table.HasCheckConstraint("CK_DiscoveryGeneration_Status", "\"Status\" IN ('Accepted','Staging','Preparing','Ready','Published','Failed','Cancelled','Superseded','Abandoned')");
                table.HasCheckConstraint("CK_DiscoveryGeneration_Hosting", "(\"RequestingIssuer\" IS NULL AND \"RequestingSubject\" IS NULL AND \"ExecutionSlot\" IS NULL AND \"ExecutorId\" IS NULL AND \"LeaseExpiresAtUtc\" IS NULL AND \"ReceiptExpiresAtUtc\" IS NULL AND \"PublicationAttemptedAtUtc\" IS NULL AND NOT \"PublicationUncertain\" AND \"Status\" <> 'Accepted') OR (\"RequestingIssuer\" IS NOT NULL AND length(\"RequestingIssuer\") > 0 AND \"RequestingSubject\" IS NOT NULL AND length(\"RequestingSubject\") > 0 AND \"ReceiptExpiresAtUtc\" IS NOT NULL AND \"ReceiptExpiresAtUtc\" > \"ExpiresAtUtc\" AND (\"ExecutionSlot\" IS NULL OR \"ExecutionSlot\" = 1) AND (\"ExecutionSlot\" IS NULL OR \"LeaseExpiresAtUtc\" IS NOT NULL) AND (\"Status\" <> 'Accepted' OR (\"ExecutionSlot\" = 1 AND \"ExecutorId\" IS NULL)) AND (\"Status\" NOT IN ('Staging','Preparing','Ready') OR (\"ExecutionSlot\" = 1 AND \"ExecutorId\" IS NOT NULL AND \"LeaseExpiresAtUtc\" IS NOT NULL)))");
                // Explicit null tests are necessary: PostgreSQL CHECK accepts UNKNOWN as well as TRUE.
                table.HasCheckConstraint("CK_DiscoveryGeneration_HostingPresence", "\"RequestingIssuer\" IS NULL OR ((\"Status\" NOT IN ('Accepted','Staging','Preparing','Ready') OR \"ExecutionSlot\" IS NOT NULL) AND (NOT \"PublicationUncertain\" OR \"PublicationAttemptedAtUtc\" IS NOT NULL) AND substring(\"RefreshId\"::text, 15, 1) = '7' AND substring(\"RefreshId\"::text, 20, 1) IN ('8','9','a','b'))");
                table.HasCheckConstraint("CK_DiscoveryGeneration_Versions", "\"Version\" > 0 AND \"ProviderVersion\" > 0 AND \"FenceToken\" > 0 AND \"ExpectedPublicationVersion\" >= 0 AND (\"ManagedSecretVersion\" IS NULL OR \"ManagedSecretVersion\" > 0)");
                table.HasCheckConstraint("CK_DiscoveryGeneration_Credential", "(\"CredentialKind\" = 'FlowPlane' AND \"ManagedSecretVersion\" IS NOT NULL) OR (\"CredentialKind\" = 'Environment' AND \"ManagedSecretVersion\" IS NULL)");
                table.HasCheckConstraint("CK_DiscoveryGeneration_Counts", "\"ObservationCount\" >= 0 AND \"CandidateCount\" >= 0 AND \"CandidateCount\" <= \"ObservationCount\" AND \"IncompatibleCount\" >= 0 AND \"IncompatibleCount\" <= \"ObservationCount\" AND \"AddedCount\" >= 0 AND \"ChangedCount\" >= 0 AND \"UnchangedCount\" >= 0 AND \"NotObservedCount\" >= 0");
                table.HasCheckConstraint("CK_DiscoveryGeneration_Expiry", "\"ExpiresAtUtc\" > \"StartedAtUtc\"");
                table.HasCheckConstraint("CK_DiscoveryGeneration_Publication", "\"Status\" <> 'Published' OR (\"PublishedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NOT NULL AND \"ScanFinishedAtUtc\" IS NOT NULL AND \"ScanCompleted\" AND \"SourceCompatibility\" IS NOT NULL AND \"SourceCompatibility\" = 'Compatible' AND \"ScanOutcome\" IS NOT NULL AND ((\"ScanOutcome\" = 'Succeeded' AND \"IncompatibleCount\" = 0) OR (\"ScanOutcome\" = 'Partial' AND \"IncompatibleCount\" > 0)))");
            });
            builder.HasKey(row => row.RefreshId);
            builder.HasAlternateKey(row => new { row.ProviderInstanceId, row.RefreshId });
            builder.HasOne<ProviderInstance>().WithMany().HasForeignKey(row => row.ProviderInstanceId).OnDelete(DeleteBehavior.Restrict);
            builder.Property(row => row.ProviderType).HasMaxLength(64);
            builder.Property(row => row.CredentialKind).HasMaxLength(16);
            builder.Property(row => row.Status).HasConversion<string>().HasMaxLength(16);
            builder.Property(row => row.ScanOutcome).HasConversion<string>().HasMaxLength(16);
            builder.Property(row => row.SourceCompatibility).HasConversion<string>().HasMaxLength(16);
            builder.Property(row => row.Error).HasConversion<string>().HasMaxLength(32);
            builder.Property(row => row.Version).IsConcurrencyToken();
            builder.Property(row => row.RequestingIssuer).HasMaxLength(2048).UseCollation("C");
            builder.Property(row => row.RequestingSubject).HasMaxLength(512).UseCollation("C");
            builder.HasIndex(row => row.ExecutionSlot).IsUnique().HasDatabaseName("UX_DiscoveryGeneration_HostedSlot")
                .HasFilter("\"ExecutionSlot\" IS NOT NULL");
            builder.HasIndex(row => row.ProviderInstanceId).IsUnique()
                .HasDatabaseName("UX_DiscoveryGeneration_ActiveClaim")
                .HasFilter("\"Status\" IN ('Accepted', 'Staging', 'Preparing', 'Ready')");
            builder.HasIndex(row => new { row.Status, row.ExpiresAtUtc });
            builder.HasIndex(row => new { row.ProviderInstanceId, row.FinishedAtUtc });
        });
        model.Entity<DiscoveryObservationRecord>(builder =>
        {
            builder.ToTable("ProviderDiscoveryObservations", table =>
            {
                table.HasCheckConstraint("CK_DiscoveryObservation_Digests", "octet_length(\"NativeKeyDigest\") = 32 AND (\"ContentFingerprint\" IS NULL OR octet_length(\"ContentFingerprint\") = 32)");
                table.HasCheckConstraint("CK_DiscoveryObservation_Identity", "length(\"NativeKey\") > 0 AND length(\"SchemaId\") > 0 AND \"ObservedProviderVersion\" > 0 AND (\"ObservedSecretVersion\" IS NULL OR \"ObservedSecretVersion\" > 0)");
                table.HasCheckConstraint("CK_DiscoveryObservation_Payload", "(\"Visibility\" = 'Observed' AND \"MetadataJson\" IS NOT NULL AND \"ContentFingerprint\" IS NOT NULL AND \"NotObservedAtUtc\" IS NULL AND ((\"CandidateSchemaId\" IS NULL AND \"CandidateJson\" IS NULL) OR (\"CandidateSchemaId\" IS NOT NULL AND \"CandidateJson\" IS NOT NULL))) OR (\"Visibility\" = 'NotObserved' AND \"MetadataJson\" IS NULL AND \"ContentFingerprint\" IS NULL AND \"CandidateSchemaId\" IS NULL AND \"CandidateJson\" IS NULL AND \"NotObservedAtUtc\" IS NOT NULL)");
            });
            builder.HasKey(row => new { row.ProviderInstanceId, row.RefreshId, row.NativeKeyDigest });
            builder.HasOne<DiscoveryGeneration>().WithMany().HasForeignKey(row => new { row.ProviderInstanceId, row.RefreshId })
                .HasPrincipalKey(row => new { row.ProviderInstanceId, row.RefreshId }).OnDelete(DeleteBehavior.Restrict);
            builder.Property(row => row.NativeKey).UseCollation("C");
            builder.Property(row => row.SchemaId).HasMaxLength(256);
            builder.Property(row => row.CandidateSchemaId).HasMaxLength(256);
            builder.Property(row => row.MetadataJson).HasColumnType("jsonb");
            builder.Property(row => row.CandidateJson).HasColumnType("jsonb");
            builder.Property(row => row.Visibility).HasConversion<string>().HasMaxLength(16);
            builder.HasIndex(row => new { row.ProviderInstanceId, row.RefreshId, row.Visibility });
            builder.HasIndex(row => new { row.ProviderInstanceId, row.RefreshId, row.CandidateSchemaId })
                .HasFilter("\"Visibility\" = 'Observed' AND \"CandidateSchemaId\" IS NOT NULL");
        });
    }
}
