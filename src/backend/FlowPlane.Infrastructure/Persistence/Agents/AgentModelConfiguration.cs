using FlowPlane.Application.Agents;
using FlowPlane.Domain.Providers;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowPlane.Infrastructure.Persistence.Agents;

internal static class AgentModelConfiguration
{
    private const string LiveGrant = "\"State\" IN ('Issued','Claimed','Proven')";

    public static void Configure(ModelBuilder model)
    {
        var agents = Table<AgentRecord>(model, "Agents");
        agents.HasKey(x => x.Id);
        agents.Property(x => x.Version).IsConcurrencyToken();
        agents.Property(x => x.AuthorizationVersion).IsConcurrencyToken();
        agents.Property(x => x.DisplayName).HasMaxLength(200);
        agents.Property(x => x.PackageVersion).HasMaxLength(64);
        Actor(agents.Property(x => x.RevokedByIssuer), agents.Property(x => x.RevokedBySubject));
        State(agents.Property(x => x.State)); State(agents.Property(x => x.Readiness));
        Check(agents, "Agents", "Versions", "\"Version\" > 0 AND \"AuthorizationVersion\" > 0 AND \"SessionFence\" > 0 AND \"PollSequence\" >= 0");
        Check(agents, "Agents", "States", Values<AgentLifecycleState>("State") + " AND " + Values<AgentReadiness>("Readiness"));
        Check(agents, "Agents", "Display", "length(\"DisplayName\") BETWEEN 1 AND 200");
        Check(agents, "Agents", "Lifetime", "\"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
        var session = new[] { "CurrentBootId", "ConnectRequestId", "SessionId", "ConnectDigest", "ConnectAcceptedAtUtc", "ConnectLeaseExpiresAtUtc",
            "ConnectAuthorizationVersion", "ConnectFence", "LeaseAuthorizationVersion", "LeaseExpiresAtUtc" };
        Check(agents, "Agents", "Session", AllOrNone(session) + " AND (\"SessionId\" IS NULL OR (octet_length(\"ConnectDigest\") = 32 AND " +
            "\"ConnectFence\" > 0 AND \"ConnectFence\" <= \"SessionFence\" AND \"ConnectAuthorizationVersion\" > 0 AND " +
            "\"LeaseAuthorizationVersion\" = \"ConnectAuthorizationVersion\" AND \"ConnectLeaseExpiresAtUtc\" > \"ConnectAcceptedAtUtc\" AND " +
            "\"LeaseExpiresAtUtc\" >= \"ConnectLeaseExpiresAtUtc\" AND \"ProtocolVersion\" IS NOT NULL))");
        Check(agents, "Agents", "Package", "num_nonnulls(\"ProtocolVersion\",\"PackageVersion\") IN (0,2) AND " +
            "(\"ProtocolVersion\" IS NULL OR (\"ProtocolVersion\" = 1 AND length(\"PackageVersion\") BETWEEN 1 AND 64))");
        var poll = new[] { "PollRequestId", "PollDigest", "PollAcknowledgedAtUtc", "PollLeaseExpiresAtUtc", "PollFence", "PollAuthorizationVersion", "NextPollSeconds" };
        Check(agents, "Agents", "Poll", AllOrNone(poll) + " AND ((\"PollSequence\" = 0 AND \"PollRequestId\" IS NULL) OR " +
            "(\"PollSequence\" > 0 AND \"PollRequestId\" IS NOT NULL AND \"SessionId\" IS NOT NULL AND octet_length(\"PollDigest\") = 32 AND " +
            "\"PollFence\" = \"ConnectFence\" AND \"PollAuthorizationVersion\" = \"ConnectAuthorizationVersion\" AND \"NextPollSeconds\" = 5 AND " +
            "\"PollLeaseExpiresAtUtc\" > \"PollAcknowledgedAtUtc\"))");
        Check(agents, "Agents", "Revocation", AllOrNone(["RevokedAtUtc", "RevokedByIssuer", "RevokedBySubject"]) +
            " AND ((\"State\" = 'Revoked') = (\"RevokedAtUtc\" IS NOT NULL))");

        var credentials = Table<AgentCredentialRecord>(model, "AgentCredentials");
        credentials.HasKey(x => x.Id); credentials.HasAlternateKey(x => new { x.AgentId, x.Id });
        credentials.HasOne<AgentRecord>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        credentials.Property(x => x.Version).IsConcurrencyToken(); State(credentials.Property(x => x.State));
        credentials.HasIndex(x => x.DerSha256).IsUnique().HasDatabaseName("UX_AgentCredential_Der");
        credentials.HasIndex(x => x.AgentId).IsUnique().HasFilter("\"State\" = 'Active'").HasDatabaseName("UX_AgentCredential_Active");
        credentials.HasIndex(x => new { x.AgentId, x.State });
        Check(credentials, "AgentCredentials", "Der", "octet_length(\"Der\") BETWEEN 1 AND 16384");
        Check(credentials, "AgentCredentials", "Digests", "octet_length(\"DerSha256\") = 32 AND octet_length(\"SpkiSha256\") = 32");
        Check(credentials, "AgentCredentials", "State", Values<AgentCredentialState>("State") + " AND \"Version\" > 0");
        Check(credentials, "AgentCredentials", "Validity", "\"NotAfterUtc\" > \"NotBeforeUtc\"");
        Check(credentials, "AgentCredentials", "Lifecycle", "(\"State\" NOT IN ('Active','RetiringReceiptsOnly','Retired') OR " +
            "(\"ProvedAtUtc\" IS NOT NULL AND \"ActivatedAtUtc\" IS NOT NULL AND \"ActivatedAtUtc\" >= \"ProvedAtUtc\")) AND " +
            "(\"State\" <> 'RetiringReceiptsOnly' OR (\"RetiringUntilUtc\" IS NOT NULL AND \"RetiringUntilUtc\" > \"ActivatedAtUtc\")) AND " +
            "((\"State\" = 'Revoked') = (\"RevokedAtUtc\" IS NOT NULL))");

        var assignments = Table<AgentAssignmentRecord>(model, "AgentProviderAssignments");
        assignments.HasKey(x => x.ProviderInstanceId); assignments.HasAlternateKey(x => new { x.ProviderInstanceId, x.AgentId });
        assignments.HasOne<ProviderInstance>().WithMany().HasForeignKey(x => x.ProviderInstanceId).OnDelete(DeleteBehavior.Restrict);
        assignments.HasOne<AgentRecord>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        assignments.Property(x => x.Version).IsConcurrencyToken(); State(assignments.Property(x => x.State));
        assignments.HasIndex(x => new { x.AgentId, x.State });
        Check(assignments, "AgentProviderAssignments", "State", Values<AgentAssignmentState>("State"));
        Check(assignments, "AgentProviderAssignments", "Versions", "\"Version\" > 0 AND \"ProviderVersion\" > 0 AND " +
            "(\"ProviderCredentialVersion\" IS NULL OR \"ProviderCredentialVersion\" > 0) AND \"UpdatedAtUtc\" >= \"CreatedAtUtc\"");

        var grants = Table<AgentGrantRecord>(model, "AgentEnrollmentGrants");
        grants.HasKey(x => x.Id); grants.Property(x => x.Version).IsConcurrencyToken();
        State(grants.Property(x => x.State)); State(grants.Property(x => x.Kind));
        Actor(grants.Property(x => x.Issuer), grants.Property(x => x.Subject));
        grants.HasOne<AgentRecord>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        grants.HasOne<AgentAssignmentRecord>().WithMany().HasForeignKey(x => new { x.ProviderInstanceId, x.AgentId })
            .HasPrincipalKey(x => new { x.ProviderInstanceId, x.AgentId }).OnDelete(DeleteBehavior.Restrict);
        grants.HasOne<AgentCredentialRecord>().WithMany().HasForeignKey(x => new { x.AgentId, x.ClaimedCredentialId })
            .HasPrincipalKey(x => new { x.AgentId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        grants.HasOne<AgentCredentialRecord>().WithMany().HasForeignKey(x => new { x.AgentId, x.ExpectedActiveCredentialId })
            .HasPrincipalKey(x => new { x.AgentId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        grants.HasOne<AgentCredentialRecord>().WithMany().HasForeignKey(x => new { x.AgentId, x.InitiatingCredentialId })
            .HasPrincipalKey(x => new { x.AgentId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        grants.HasIndex(x => x.AgentId).IsUnique().HasFilter(LiveGrant).HasDatabaseName("UX_AgentGrant_LiveAgent");
        grants.HasIndex(x => x.ProviderInstanceId).IsUnique().HasFilter("\"Kind\" = 'Enrollment' AND " + LiveGrant).HasDatabaseName("UX_AgentGrant_LiveProvider");
        grants.HasIndex(x => new { x.ActorDigest, x.ClientRequestId }).IsUnique().HasFilter("\"Kind\" = 'Enrollment'").HasDatabaseName("UX_AgentGrant_ActorRequest");
        grants.HasIndex(x => new { x.InitiatingCredentialId, x.ClientRequestId }).IsUnique().HasFilter("\"Kind\" = 'Rotation'").HasDatabaseName("UX_AgentGrant_RotationRequest");
        grants.HasIndex(x => x.ClaimId).IsUnique().HasFilter("\"ClaimId\" IS NOT NULL");
        grants.HasIndex(x => x.ProofRequestId).IsUnique().HasFilter("\"ProofRequestId\" IS NOT NULL");
        grants.HasIndex(x => new { x.State, x.ExpiresAtUtc, x.Id });
        Check(grants, "AgentEnrollmentGrants", "State", Values<AgentGrantState>("State") + " AND " + Values<AgentGrantKind>("Kind"));
        Check(grants, "AgentEnrollmentGrants", "Versions", "\"Version\" > 0 AND \"ExpectedAgentVersion\" > 0 AND \"ExpectedAuthorizationVersion\" > 0 AND " +
            "\"ExpectedAssignmentVersion\" > 0 AND \"ProviderVersion\" > 0 AND (\"ProviderCredentialVersion\" IS NULL OR \"ProviderCredentialVersion\" > 0)");
        Check(grants, "AgentEnrollmentGrants", "Lifetime", "\"ExpiresAtUtc\" > \"CreatedAtUtc\" AND \"ExpiresAtUtc\" <= \"CreatedAtUtc\" + interval '10 minutes'");
        Check(grants, "AgentEnrollmentGrants", "Digests", "octet_length(\"RequestDigest\") = 32 AND (\"TokenHash\" IS NULL OR octet_length(\"TokenHash\") = 32)");
        Check(grants, "AgentEnrollmentGrants", "Kind", "(\"Kind\" = 'Enrollment' AND \"Issuer\" IS NOT NULL AND length(\"Issuer\") > 0 AND \"Subject\" IS NOT NULL AND " +
            "length(\"Subject\") > 0 AND \"ActorDigest\" IS NOT NULL AND octet_length(\"ActorDigest\") = 32 AND \"InitiatingCredentialId\" IS NULL AND " +
            "\"ExpectedActiveCredentialId\" IS NULL AND (\"State\" <> 'Issued' OR \"TokenHash\" IS NOT NULL)) OR " +
            "(\"Kind\" = 'Rotation' AND \"Issuer\" IS NULL AND \"Subject\" IS NULL AND \"ActorDigest\" IS NULL AND \"TokenHash\" IS NULL AND " +
            "\"InitiatingCredentialId\" IS NOT NULL AND \"ExpectedActiveCredentialId\" IS NOT NULL AND \"InitiatingCredentialId\" = \"ExpectedActiveCredentialId\")");
        Check(grants, "AgentEnrollmentGrants", "Claim", AllOrNone(["ClaimId", "ClaimDigest", "ClaimedCredentialId", "Nonce", "ClaimReceiptVersion", "ClaimedAtUtc"]) +
            " AND (\"ClaimId\" IS NULL OR (octet_length(\"ClaimDigest\") = 32 AND octet_length(\"Nonce\") = 32 AND \"ClaimReceiptVersion\" > 0 AND " +
            "\"ClaimedAtUtc\" >= \"CreatedAtUtc\" AND \"ClaimedAtUtc\" < \"ExpiresAtUtc\")) AND " +
            "(\"State\" NOT IN ('Claimed','Proven','Redeemed') OR \"ClaimId\" IS NOT NULL)");
        Check(grants, "AgentEnrollmentGrants", "Proof", AllOrNone(["ProofRequestId", "ProofReceiptVersion", "ProvedAtUtc"]) +
            " AND (\"ProofRequestId\" IS NULL OR (\"ClaimId\" IS NOT NULL AND \"ProofReceiptVersion\" > \"ClaimReceiptVersion\" AND " +
            "\"ProvedAtUtc\" >= \"ClaimedAtUtc\" AND \"ProvedAtUtc\" < \"ExpiresAtUtc\")) AND " +
            "(\"State\" NOT IN ('Proven','Redeemed') OR \"ProofRequestId\" IS NOT NULL)");
        Check(grants, "AgentEnrollmentGrants", "Redemption", AllOrNone(["RedemptionReceiptVersion", "ActivatedAuthorizationVersion", "RedeemedAtUtc"]) +
            " AND ((\"State\" = 'Redeemed') = (\"RedeemedAtUtc\" IS NOT NULL)) AND " +
            "(\"RedeemedAtUtc\" IS NULL OR (\"ProofRequestId\" IS NOT NULL AND \"RedemptionReceiptVersion\" > \"ProofReceiptVersion\" AND " +
            "\"ActivatedAuthorizationVersion\" > \"ExpectedAuthorizationVersion\" AND \"RedeemedAtUtc\" >= \"ProvedAtUtc\" AND \"RedeemedAtUtc\" < \"ExpiresAtUtc\"))");

        var events = Table<AgentSecurityEventRecord>(model, "AgentSecurityEvents");
        events.HasKey(x => x.Id); State(events.Property(x => x.Kind));
        Actor(events.Property(x => x.Issuer), events.Property(x => x.Subject));
        events.HasOne<AgentRecord>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        events.HasOne<AgentCredentialRecord>().WithMany().HasForeignKey(x => new { x.AgentId, x.CredentialId })
            .HasPrincipalKey(x => new { x.AgentId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        events.HasIndex(x => new { x.AgentId, x.RecordedAtUtc, x.Id });
        Check(events, "AgentSecurityEvents", "Facts", Values<AgentSecurityEventKind>("Kind") +
            " AND \"AuthorizationVersion\" > 0 AND \"Fence\" > 0 AND \"PreviousAuthorizationVersion\" > 0 AND \"PreviousFence\" > 0 AND " +
            "\"PreviousAuthorizationVersion\" <= \"AuthorizationVersion\" AND \"PreviousFence\" <= \"Fence\" AND num_nonnulls(\"Issuer\",\"Subject\") IN (0,2)");
    }

    private static EntityTypeBuilder<T> Table<T>(ModelBuilder model, string table) where T : class
    {
        var builder = model.Entity<T>(); builder.ToTable(table);
        foreach (var property in typeof(T).GetProperties().Where(p => p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?)))
            Check(builder, table, property.Name, $"\"{property.Name}\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        return builder;
    }
    private static void Check<T>(EntityTypeBuilder<T> builder, string table, string name, string sql) where T : class =>
        builder.ToTable(table, options => options.HasCheckConstraint($"CK_{table}_{name}", sql));
    private static void Actor(PropertyBuilder<string?> issuer, PropertyBuilder<string?> subject)
    { issuer.HasMaxLength(2048).UseCollation("C"); subject.HasMaxLength(512).UseCollation("C"); }
    private static void State<T>(PropertyBuilder<T> property) where T : struct, Enum => property.HasConversion<string>().HasMaxLength(32);
    private static string Values<T>(string property) where T : struct, Enum => $"\"{property}\" IN ({string.Join(',', Enum.GetNames<T>().Select(x => $"'{x}'"))})";
    private static string AllOrNone(string[] fields) => $"num_nonnulls({string.Join(',', fields.Select(x => $"\"{x}\""))}) IN (0,{fields.Length})";
}
