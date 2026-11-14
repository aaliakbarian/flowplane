using System.Data.Common;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using FlowPlane.Application.Agents;
using FlowPlane.Application.Providers;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Infrastructure.Persistence.Agents;
using FlowPlane.Providers.Ssis.Configuration;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace FlowPlane.Persistence.Tests;

public sealed class AgentControlTransactionPostgreSqlTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly AgentActor Actor = new("https://issuer.example/", "synthetic-operator");
    private static readonly byte[] Hash = SHA256.HashData("synthetic-token"u8);
    private const string Previous = "20261026084710_DurableDiscoveryHosting";
    private const string Target = "20261111081939_AgentControlFoundation";

    [Fact]
    public async Task Actual_catalog_columns_checks_indexes_and_restrictive_FKs_match_the_reviewed_migration()
    {
        await using var fixture = await Fixture.Create();
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[Target], db.Database.ProviderName!);
        foreach (var table in migration.UpOperations.OfType<CreateTableOperation>())
        {
            var columns = await db.Database.SqlQuery<string>($"""
                SELECT a.attname || ':' || format_type(a.atttypid,a.atttypmod) || ':' || a.attnotnull::text AS "Value"
                FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='public' AND c.relname={table.Name} AND a.attnum>0 AND NOT a.attisdropped
                ORDER BY a.attname COLLATE "C"
                """).ToListAsync(Token);
            Assert.Equal(table.Columns.Select(c => $"{c.Name}:{c.ColumnType}:{(!c.IsNullable).ToString().ToLowerInvariant()}").Order(StringComparer.Ordinal), columns);
            var constraints = await db.Database.SqlQuery<string>($"""
                SELECT conname || ':' || contype::text || ':' || convalidated::text || ':' || confdeltype::text AS "Value"
                FROM pg_constraint WHERE conrelid=CAST({"public.\"" + table.Name + "\""} AS regclass)
                ORDER BY conname COLLATE "C"
                """).ToListAsync(Token);
            var expected = table.CheckConstraints.Select(c => c.Name + ":c:true: ")
                .Concat(table.Columns.Where(c => !c.IsNullable).Select(c => table.Name + "_" + c.Name + "_not_null:n:true: "))
                .Concat(table.ForeignKeys.Select(c => c.Name + ":f:true:r"))
                .Concat(table.UniqueConstraints.Select(c => c.Name + ":u:true: "))
                .Append(table.PrimaryKey!.Name + ":p:true: ").Order(StringComparer.Ordinal);
            Assert.Equal(expected, constraints);
        }
        foreach (var index in migration.UpOperations.OfType<CreateIndexOperation>())
        {
            var facts = await db.Database.SqlQuery<string>($"""
                SELECT i.indisunique::text || ':' || i.indisvalid::text || ':' || i.indisready::text || ':' ||
                  (i.indpred IS NOT NULL)::text || ':' || string_agg(a.attname,',' ORDER BY k.ordinality) AS "Value"
                FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid
                JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum,ordinality) ON true
                JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum
                WHERE c.relname={index.Name} GROUP BY i.indexrelid,i.indisunique,i.indisvalid,i.indisready,(i.indpred IS NOT NULL)
                """).SingleAsync(Token);
            Assert.Equal($"{index.IsUnique.ToString().ToLowerInvariant()}:true:true:{(index.Filter is not null).ToString().ToLowerInvariant()}:{string.Join(',', index.Columns)}", facts);
        }
        var definitions = await db.Database.SqlQueryRaw<string>("""
            SELECT 'constraint:' || conname || ':' || pg_get_constraintdef(oid) AS "Value"
            FROM pg_constraint WHERE conrelid IN (SELECT oid FROM pg_class WHERE relname IN
              ('Agents','AgentCredentials','AgentProviderAssignments','AgentEnrollmentGrants','AgentSecurityEvents'))
            UNION ALL SELECT 'index:' || indexdef FROM pg_indexes WHERE tablename LIKE 'Agent%'
            """).ToArrayAsync(Token);
        fixture.Database.RecordEvidence("catalog-verified", definitions);
        await using var sqlDatabase = await AgentControlPostgreSqlTests.Create();
        await using var sqlServices = sqlDatabase.CreateServices(null);
        await using var sqlScope = sqlServices.CreateAsyncScope();
        var reference = sqlScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        await reference.GetService<IMigrator>().MigrateAsync(Previous, Token);
        await reference.Database.ExecuteSqlRawAsync(AgentControlPostgreSqlTests.ReviewedSql("up"), Token);
        // Exact catalog comparison: expressions, FK columns/actions, partial predicates and defaults are not normalized away.
        const string catalog = """
            SELECT value AS "Value" FROM (
              SELECT 'constraint:' || conname || ':' || pg_get_constraintdef(oid) AS value FROM pg_constraint
                WHERE conrelid IN (SELECT oid FROM pg_class WHERE relname IN
                  ('Agents','AgentCredentials','AgentProviderAssignments','AgentEnrollmentGrants','AgentSecurityEvents'))
              UNION ALL SELECT 'index:' || indexdef FROM pg_indexes WHERE schemaname='public' AND tablename LIKE 'Agent%'
              UNION ALL SELECT 'column:' || c.relname || ':' || a.attname || ':' || format_type(a.atttypid,a.atttypmod) || ':' ||
                a.attnotnull::text || ':' || coalesce(pg_get_expr(d.adbin,d.adrelid),'') FROM pg_class c
                JOIN pg_attribute a ON a.attrelid=c.oid LEFT JOIN pg_attrdef d ON d.adrelid=c.oid AND d.adnum=a.attnum
                WHERE c.relnamespace='public'::regnamespace AND c.relkind='r' AND c.relname LIKE 'Agent%'
                  AND a.attnum>0 AND NOT a.attisdropped
            ) facts ORDER BY value COLLATE "C"
            """;
        Assert.Equal(await reference.Database.SqlQueryRaw<string>(catalog).ToListAsync(Token),
            await db.Database.SqlQueryRaw<string>(catalog).ToListAsync(Token));
    }

    [Fact]
    public async Task PostgreSQL_rejects_cross_agent_ownership_bad_bounds_duplicate_pins_active_keys_and_live_grants()
    {
        await using var fixture = await Fixture.Create();
        var (certificate, _) = await fixture.Enroll();
        var otherProvider = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(fixture.Services, Token);
        var otherGrant = await fixture.Store(s => s.IssueGrantAsync(new(new(otherProvider, 1), Actor, Guid.NewGuid(), "Other", Hash), Token));
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        await Reject($"UPDATE \"AgentEnrollmentGrants\" SET \"AgentId\"={certificate.AgentId} WHERE \"Id\"={otherGrant.GrantId}", PostgresErrorCodes.ForeignKeyViolation);
        await Reject($"UPDATE \"AgentCredentials\" SET \"DerSha256\"={new byte[31]} WHERE \"Id\"={certificate.CredentialId}", PostgresErrorCodes.CheckViolation);
        await Reject($"UPDATE \"Agents\" SET \"SessionId\"={Guid.NewGuid()} WHERE \"Id\"={certificate.AgentId}", PostgresErrorCodes.CheckViolation);
        await Reject($"UPDATE \"AgentEnrollmentGrants\" SET \"TokenHash\"=NULL WHERE \"Id\"={otherGrant.GrantId}", PostgresErrorCodes.CheckViolation);
        await Reject($"UPDATE \"AgentProviderAssignments\" SET \"ProviderVersion\"=0 WHERE \"ProviderInstanceId\"={fixture.ProviderId}", PostgresErrorCodes.CheckViolation);
        var credential = await db.AgentCredentials.AsNoTracking().SingleAsync(c => c.Id == certificate.CredentialId, Token);
        await Duplicate(credential with { Id = Guid.NewGuid(), AgentId = otherGrant.AgentId }); // Global DER pin uniqueness.
        await Duplicate(credential with { Id = Guid.NewGuid(), Der = [1], DerSha256 = SHA256.HashData(new byte[] { 1 }) }); // Active-key uniqueness.
        var grant = await db.AgentGrants.AsNoTracking().SingleAsync(g => g.Id == otherGrant.GrantId, Token);
        db.AgentGrants.Add(grant with { Id = Guid.NewGuid(), ClientRequestId = Guid.NewGuid() });
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token))).InnerException).SqlState);
        async Task Reject(FormattableString sql, string state) => Assert.Equal(state,
            (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql, Token))).SqlState);
        async Task Duplicate(AgentCredentialRecord copy)
        {
            db.AgentCredentials.Add(copy);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token))).InnerException).SqlState);
            db.ChangeTracker.Clear();
        }
    }

    [Fact]
    public async Task Concurrent_actors_and_claiming_keys_cannot_steal_a_grant_or_duplicate_proof()
    {
        await using var fixture = await Fixture.Create();
        var request = new IssueAgentGrant(new(fixture.ProviderId, 1), Actor, Guid.NewGuid(), "Synthetic", Hash);
        var actors = await Task.WhenAll(Capture(() => fixture.Store(s => s.IssueGrantAsync(request, Token))),
            Capture(() => fixture.Store(s => s.IssueGrantAsync(request with { Actor = new(Actor.Issuer, "other"), ClientRequestId = Guid.NewGuid() }, Token))));
        Assert.Single(actors, x => x.Error is null);
        Assert.Equal(AgentStoreError.Conflict, Assert.Single(actors, x => x.Error is not null).Error);
        var issued = actors.Single(x => x.Value is not null).Value!;
        var a = new ClaimAgentGrant(issued.GrantId, Guid.NewGuid(), Hash, Certificate());
        var b = new ClaimAgentGrant(issued.GrantId, Guid.NewGuid(), Hash, Certificate());
        var claims = await Task.WhenAll(Capture(() => fixture.Store(s => s.ClaimAsync(a, Token))), Capture(() => fixture.Store(s => s.ClaimAsync(b, Token))));
        Assert.Single(claims, x => x.Error is null);
        Assert.Equal(AgentStoreError.Conflict, Assert.Single(claims, x => x.Error is not null).Error);
        var accepted = claims.Single(x => x.Value is not null).Value!;
        var input = accepted.ClaimId == a.ClaimId ? a : b;
        var proof = new ProveAgentGrant(issued.GrantId, input.ClaimId, Guid.NewGuid(), accepted.Nonce!,
            new(issued.AgentId, accepted.CredentialId!.Value, input.CertificateDer));
        var proofs = await Task.WhenAll(fixture.Store(s => s.RecordProofAsync(proof, Token)), fixture.Store(s => s.RecordProofAsync(proof, Token)));
        Assert.Equal(JsonSerializer.Serialize(proofs[0]), JsonSerializer.Serialize(proofs[1]));
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Equal(1, await db.AgentCredentials.CountAsync(Token));
        Assert.Equal(1, await db.AgentSecurityEvents.CountAsync(e => e.Kind == AgentSecurityEventKind.PossessionProved, Token));
    }

    [Fact]
    public async Task Concurrent_expired_lease_acquisitions_have_one_winner_and_old_owner_cannot_renew()
    {
        await using var fixture = await Fixture.Create();
        var (certificate, _) = await fixture.Enroll();
        var request = await fixture.ConnectInput(certificate);
        var original = (await fixture.Store(s => s.ConnectAsync(request, Token))).Receipt!;
        await fixture.Sql($"UPDATE \"Agents\" SET \"ConnectAcceptedAtUtc\"=clock_timestamp()-interval '2 minutes', \"ConnectLeaseExpiresAtUtc\"=clock_timestamp()-interval '1 minute', \"LeaseExpiresAtUtc\"=clock_timestamp()-interval '1 minute' WHERE \"Id\"={certificate.AgentId}");
        var replacement = request with { BootId = Guid.NewGuid(), RequestId = Guid.NewGuid(), ExpectedFence = original.Fence };
        var results = await Task.WhenAll(Capture(() => fixture.Store(s => s.ConnectAsync(replacement, Token))),
            Capture(() => fixture.Store(s => s.ConnectAsync(replacement with { BootId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, Token))));
        var winner = Assert.Single(results, r => r.Error is null).Value!.Receipt!;
        Assert.Equal(AgentStoreError.ContextChanged, Assert.Single(results, r => r.Error is not null).Error);
        Assert.True(winner.Fence > original.Fence);
        Assert.NotEqual(winner.SessionId, original.SessionId);
        var poll = new PollAgent(certificate, original.SessionId, original.Fence, original.AuthorizationVersion, Guid.NewGuid(), 1, AgentReadiness.ReadyForControlPlane, true, 0);
        Assert.Equal(AgentStoreError.ContextChanged, (await Capture(() => fixture.Store(s => s.PollAsync(poll, Token)))).Error);
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("journal")]
    [InlineData("overflow")]
    public async Task Live_conflict_missing_evidence_or_counter_exhaustion_never_grants_authority(string scenario)
    {
        await using var fixture = await Fixture.Create();
        var (certificate, _) = await fixture.Enroll();
        var request = await fixture.ConnectInput(certificate);
        if (scenario == "conflict")
        {
            var first = (await fixture.Store(s => s.ConnectAsync(request, Token))).Receipt!;
            var denied = await fixture.Store(s => s.ConnectAsync(request with { BootId = Guid.NewGuid(), RequestId = Guid.NewGuid(), ExpectedFence = first.Fence }, Token));
            Assert.Equal(AgentStoreError.ConflictingIncarnation, denied.Error);
            Assert.Equal(AgentStoreError.ActionRequired, (await Capture(() => fixture.Store(s => s.ConnectAsync(request, Token)))).Error);
        }
        else if (scenario == "journal")
        {
            var denied = await fixture.Store(s => s.ConnectAsync(request with { JournalReady = false }, Token));
            Assert.Equal(AgentStoreError.ActionRequired, denied.Error);
            Assert.Equal(AgentStoreError.ActionRequired, (await fixture.Store(s => s.ConnectAsync(request, Token))).Error);
        }
        else
        {
            await fixture.Sql($"UPDATE \"Agents\" SET \"SessionFence\"={long.MaxValue} WHERE \"Id\"={certificate.AgentId}");
            Assert.Equal(AgentStoreError.Capacity, (await Capture(() => fixture.Store(s => s.ConnectAsync(request with { ExpectedFence = long.MaxValue }, Token)))).Error);
        }
    }

    [Fact]
    public async Task Rotation_replay_and_revocation_race_cannot_reactivate_an_old_or_revoked_key()
    {
        await using var fixture = await Fixture.Create();
        var (old, _) = await fixture.Enroll();
        var request = await fixture.ConnectInput(old);
        var rotation = await fixture.Store(s => s.BeginRotationAsync(new(new(fixture.ProviderId, 1), old, request.ExpectedAuthorizationVersion, Guid.NewGuid()), Token));
        var claimId = Guid.NewGuid();
        var der = Certificate();
        var claim = await fixture.Store(s => s.ClaimAsync(new(rotation.GrantId, claimId, ReadOnlyMemory<byte>.Empty, der, old), Token));
        var proof = new ProveAgentGrant(rotation.GrantId, claimId, Guid.NewGuid(), claim.Nonce!, new(old.AgentId, claim.CredentialId!.Value, der));
        await fixture.Store(s => s.RecordProofAsync(proof, Token));
        var active = await fixture.Store(s => s.ActivateRotationAsync(proof, Token));
        Assert.Equal(JsonSerializer.Serialize(active), JsonSerializer.Serialize(await fixture.Store(s => s.ActivateRotationAsync(proof, Token))));
        Assert.Equal(AgentStoreError.InvalidCredential, (await Capture(() => fixture.Store(s => s.ConnectAsync(request, Token)))).Error);
        Assert.Equal(active.GrantId, (await fixture.Store(s => s.ReadGrantAsync(active.GrantId, old, Token))).GrantId);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var row = await db.Agents.AsNoTracking().SingleAsync(Token);
        var races = await Task.WhenAll(Capture(() => fixture.Store(s => s.ActivateRotationAsync(proof, Token))),
            Capture(async () => { await fixture.Store(async s => { await s.RevokeAsync(new(old.AgentId, Actor, row.Version), Token); return true; }); return active; }));
        Assert.Contains(races, r => r.Error is null);
        Assert.Equal(AgentLifecycleState.Revoked, (await db.Agents.AsNoTracking().SingleAsync(Token)).State);
        Assert.Equal(AgentStoreError.Revoked, (await Capture(() => fixture.Store(s => s.ActivateRotationAsync(proof, Token)))).Error);
        Assert.All(await db.AgentCredentials.AsNoTracking().ToListAsync(Token), c => Assert.Equal(AgentCredentialState.Revoked, c.State));
    }

    [Fact]
    public async Task Revocation_at_exhausted_live_grant_version_remains_a_durable_security_denial()
    {
        await using var fixture = await Fixture.Create();
        var grant = await fixture.Store(s => s.IssueGrantAsync(new(new(fixture.ProviderId, 1), Actor, Guid.NewGuid(), "Synthetic", Hash), Token));
        await fixture.Sql($"UPDATE \"AgentEnrollmentGrants\" SET \"Version\"={long.MaxValue} WHERE \"Id\"={grant.GrantId}");
        await fixture.Store(async s => { await s.RevokeAsync(new(grant.AgentId, Actor, 1), Token); return true; });
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Equal(AgentLifecycleState.Revoked, (await db.Agents.SingleAsync(Token)).State);
        Assert.Equal(AgentGrantState.Cancelled, (await db.AgentGrants.SingleAsync(Token)).State);
        Assert.Null((await db.AgentGrants.SingleAsync(Token)).TokenHash);
    }

    [Fact]
    public async Task Pending_rotation_activation_and_revocation_serialize_without_silent_revival()
    {
        await using var fixture = await Fixture.Create();
        var (old, _) = await fixture.Enroll();
        var context = await fixture.ConnectInput(old);
        var grant = await fixture.Store(s => s.BeginRotationAsync(new(new(fixture.ProviderId, 1), old, context.ExpectedAuthorizationVersion, Guid.NewGuid()), Token));
        var claimId = Guid.NewGuid();
        var der = Certificate();
        var claim = await fixture.Store(s => s.ClaimAsync(new(grant.GrantId, claimId, ReadOnlyMemory<byte>.Empty, der, old), Token));
        var proof = new ProveAgentGrant(grant.GrantId, claimId, Guid.NewGuid(), claim.Nonce!, new(old.AgentId, claim.CredentialId!.Value, der));
        await fixture.Store(s => s.RecordProofAsync(proof, Token));
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var before = await db.Agents.AsNoTracking().SingleAsync(Token);
        var activation = Capture(() => fixture.Store(s => s.ActivateRotationAsync(proof, Token)));
        var revocation = Capture(() => fixture.Store(async s => { await s.RevokeAsync(new(old.AgentId, Actor, before.Version), Token); return true; }));
        await Task.WhenAll(activation, revocation);
        var after = await db.Agents.AsNoTracking().SingleAsync(Token);
        if ((await revocation).Error is null)
        {
            Assert.Equal(AgentLifecycleState.Revoked, after.State);
            Assert.Equal(AgentStoreError.Revoked, (await activation).Error);
        }
        else
        {
            Assert.Null((await activation).Error);
            Assert.Equal(AgentStoreError.ContextChanged, (await revocation).Error);
            // Explicit current-version operator retry, never an implicit replay of the stale intent.
            await fixture.Store(async s => { await s.RevokeAsync(new(old.AgentId, Actor, after.Version), Token); return true; });
        }
        Assert.Equal(AgentStoreError.Revoked, (await Capture(() => fixture.Store(s => s.ActivateRotationAsync(proof, Token)))).Error);
        Assert.All(await db.AgentCredentials.AsNoTracking().ToListAsync(Token), c => Assert.Equal(AgentCredentialState.Revoked, c.State));
    }

    [Theory]
    [InlineData("configuration", "connect")]
    [InlineData("configuration", "poll")]
    [InlineData("configuration", "confirm")]
    [InlineData("credential", "connect")]
    [InlineData("credential", "poll")]
    [InlineData("credential", "confirm")]
    [InlineData("direct-secret", "connect")]
    [InlineData("direct-secret", "poll")]
    [InlineData("direct-secret", "confirm")]
    public async Task Provider_change_races_session_admission_and_atomically_fences_old_context(string change, string operation)
    {
        await using var fixture = await Fixture.Create(managed: true);
        var (certificate, confirmation) = operation == "confirm"
            ? await AgentControlPostgreSqlTests.Proved(fixture.Services, fixture.ProviderId) : await fixture.Enroll();
        var request = await fixture.ConnectInput(certificate);
        PollAgent? poll = null;
        if (operation == "poll")
        {
            var connected = (await fixture.Store(s => s.ConnectAsync(request, Token))).Receipt!;
            poll = new(certificate, connected.SessionId, connected.Fence, connected.AuthorizationVersion, Guid.NewGuid(), 1, AgentReadiness.ReadyForControlPlane, true, 0);
        }
        await using var scope = fixture.Services.CreateAsyncScope();
        var providers = scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>();
        var provider = (await providers.FindAsync(fixture.ProviderId, Token))!;
        var admission = Capture(Admit);
        if (change == "configuration")
            await providers.UpdateAsync(provider.Id, provider.Version, "Changed", true, provider.Configuration, DateTimeOffset.UtcNow, Token);
        else if (change == "credential")
            await providers.ReplaceManagedSecretAsync(provider.Id, provider.Version, "synthetic-replacement".AsMemory(), DateTimeOffset.UtcNow, Token);
        else
            await scope.ServiceProvider.GetRequiredService<IManagedSecretStore>().ReplaceAsync(provider.SecretReference, "synthetic-replacement".AsMemory(), Token);
        var result = await admission;
        var deniedBeforeAdmission = operation == "confirm" ? AgentStoreError.Conflict : AgentStoreError.ContextChanged;
        Assert.True(result.Error is null || result.Error == deniedBeforeAdmission, $"Unexpected safe code: {result.Error}");
        Assert.Equal(result.Error == AgentStoreError.Conflict ? AgentStoreError.Conflict : AgentStoreError.ContextChanged, (await Capture(Admit)).Error);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var assignment = await db.AgentAssignments.AsNoTracking().SingleAsync(Token);
        Assert.Equal(AgentAssignmentState.Suspended, assignment.State);
        if (operation == "confirm") Assert.Equal(result.Error == AgentStoreError.Conflict ? AgentGrantState.Cancelled : AgentGrantState.Redeemed,
            (await db.AgentGrants.AsNoTracking().SingleAsync(Token)).State);
        var agent = await db.Agents.AsNoTracking().SingleAsync(Token);
        Assert.True(agent.AuthorizationVersion > request.ExpectedAuthorizationVersion);
        Assert.True(agent.SessionFence > request.ExpectedFence);
        Assert.Equal(2, (await db.ProviderInstances.AsNoTracking().SingleAsync(Token)).Version);
        if (change != "configuration") Assert.Equal(2, (await db.SecretRecords.AsNoTracking().SingleAsync(Token)).Version);
        // Deletion must not retire inventory or remove its owned encrypted credential.
        var ciphertext = (await db.SecretRecords.AsNoTracking().SingleAsync(Token)).Ciphertext;
        var deletion = await Assert.ThrowsAsync<AgentStoreException>(() => providers.DeleteAsync(provider.Id, 2, Token));
        Assert.Equal(AgentStoreError.Conflict, deletion.Code);
        Assert.Equal(ciphertext, (await db.SecretRecords.AsNoTracking().SingleAsync(Token)).Ciphertext);
        Assert.Empty(await db.DiscoveryStates.AsNoTracking().ToListAsync(Token));
        async Task<bool> Admit()
        {
            if (operation == "confirm") await fixture.Store(s => s.ConfirmAsync(confirmation, Token));
            else if (operation == "poll") await fixture.Store(s => s.PollAsync(poll!, Token));
            else await fixture.Store(s => s.ConnectAsync(request, Token));
            return true;
        }
    }

    [Fact]
    public async Task Grant_expiry_at_database_time_cannot_be_extended_by_replay_or_new_proof()
    {
        await using var fixture = await Fixture.Create();
        var issued = await fixture.Store(s => s.IssueGrantAsync(new(new(fixture.ProviderId, 1), Actor, Guid.NewGuid(), "Synthetic", Hash), Token));
        await fixture.Sql($"UPDATE \"AgentEnrollmentGrants\" SET \"CreatedAtUtc\"=statement_timestamp()-interval '10 minutes', \"ExpiresAtUtc\"=statement_timestamp() WHERE \"Id\"={issued.GrantId}");
        var failure = await Capture(() => fixture.Store(s => s.ClaimAsync(new(issued.GrantId, Guid.NewGuid(), Hash, Certificate()), Token)));
        Assert.Equal(AgentStoreError.Expired, failure.Error);
        Assert.Equal(0, await fixture.Store(s => s.CleanupAsync(issued.AgentId, Token))); // Expiry transition retains receipt for 24h.
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Equal(AgentGrantState.Expired, (await db.AgentGrants.SingleAsync(Token)).State);
        Assert.Empty(await db.AgentCredentials.ToListAsync(Token));
    }

    [Fact]
    public async Task Lost_commit_acknowledgements_replay_original_receipts_without_extending_authority()
    {
        var boundary = new LostCommit();
        await using var fixture = await Fixture.Create(boundary);
        var issue = new IssueAgentGrant(new(fixture.ProviderId, 1), Actor, Guid.NewGuid(), "Synthetic", Hash);
        boundary.Armed = true;
        Assert.Equal(AgentStoreError.CommitUncertain, (await Capture(() => fixture.Store(s => s.IssueGrantAsync(issue, Token)))).Error);
        var receipt = await fixture.Store(s => s.IssueGrantAsync(issue, Token));
        var claimId = Guid.NewGuid();
        var der = Certificate();
        var claim = await fixture.Store(s => s.ClaimAsync(new(receipt.GrantId, claimId, Hash, der), Token));
        var certificate = new AgentCertificateContext(receipt.AgentId, claim.CredentialId!.Value, der);
        var proof = await fixture.Store(s => s.RecordProofAsync(new(receipt.GrantId, claimId, Guid.NewGuid(), claim.Nonce!, certificate), Token));
        await fixture.Store(s => s.ConfirmAsync(new(receipt.GrantId, Actor, proof.Version, proof.Nonce!), Token));
        var connect = await fixture.ConnectInput(certificate);
        boundary.Armed = true;
        Assert.Equal(AgentStoreError.CommitUncertain, (await Capture(() => fixture.Store(s => s.ConnectAsync(connect, Token)))).Error);
        var connected = (await fixture.Store(s => s.ConnectAsync(connect, Token))).Receipt!;
        var poll = new PollAgent(certificate, connected.SessionId, connected.Fence, connected.AuthorizationVersion, Guid.NewGuid(), 1, AgentReadiness.ReadyForControlPlane, true, 0);
        boundary.Armed = true;
        Assert.Equal(AgentStoreError.CommitUncertain, (await Capture(() => fixture.Store(s => s.PollAsync(poll, Token)))).Error);
        var acknowledged = (await fixture.Store(s => s.PollAsync(poll, Token))).Receipt!;
        Assert.Equal(connected, (await fixture.Store(s => s.ConnectAsync(connect, Token))).Receipt);
        Assert.Equal(acknowledged, (await fixture.Store(s => s.PollAsync(poll, Token))).Receipt);
        Assert.Equal(acknowledged.LeaseExpiresAtUtc, (await fixture.Store(s => s.ReadSessionAsync(certificate, Token)))!.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task Exhausted_fence_and_epoch_cannot_replay_control_receipts_after_a_recovery_hold()
    {
        await using var fixture = await Fixture.Create();
        var (certificate, _) = await fixture.Enroll();
        await fixture.Sql($"UPDATE \"Agents\" SET \"AuthorizationVersion\"={long.MaxValue}, \"SessionFence\"={long.MaxValue - 1} WHERE \"Id\"={certificate.AgentId}");
        var request = await fixture.ConnectInput(certificate);
        var connected = (await fixture.Store(s => s.ConnectAsync(request, Token))).Receipt!;
        var first = new PollAgent(certificate, connected.SessionId, connected.Fence, connected.AuthorizationVersion,
            Guid.NewGuid(), 1, AgentReadiness.ReadyForControlPlane, true, 0);
        await fixture.Store(s => s.PollAsync(first, Token));
        Assert.Equal(AgentStoreError.ActionRequired, (await fixture.Store(s => s.PollAsync(first with
        { RequestId = Guid.NewGuid(), Sequence = 2, JournalReady = false }, Token))).Error);
        Assert.Equal(AgentStoreError.ActionRequired, (await fixture.Store(s => s.ConnectAsync(request, Token))).Error);
        Assert.Equal(AgentStoreError.ActionRequired, (await fixture.Store(s => s.PollAsync(first, Token))).Error);
        Assert.False((await fixture.Store(s => s.ReadSessionAsync(certificate, Token)))!.AuthorityCurrent);
    }

    [Fact]
    public async Task Cleanup_is_bounded_preserves_active_records_and_holds_all_evidence_during_recovery()
    {
        await using var fixture = await Fixture.Create();
        var (certificate, _) = await fixture.Enroll();
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var row = await db.AgentSecurityEvents.FirstAsync(Token);
        db.AgentSecurityEvents.AddRange(Enumerable.Range(0, 120).Select(_ => row with { Id = Guid.NewGuid(), RecordedAtUtc = DateTimeOffset.UtcNow.AddDays(-91) }));
        await db.SaveChangesAsync(Token);
        await fixture.Sql($"UPDATE \"Agents\" SET \"RecoveryRequired\"=true WHERE \"Id\"={certificate.AgentId}");
        Assert.Equal(0, await fixture.Store(s => s.CleanupAsync(certificate.AgentId, Token)));
        Assert.Equal(120, await db.AgentSecurityEvents.CountAsync(e => e.RecordedAtUtc < DateTimeOffset.UtcNow.AddDays(-90), Token));
        await fixture.Sql($"UPDATE \"Agents\" SET \"RecoveryRequired\"=false WHERE \"Id\"={certificate.AgentId}");
        Assert.Equal(100, await fixture.Store(s => s.CleanupAsync(certificate.AgentId, Token)));
        Assert.Equal(20, await db.AgentSecurityEvents.CountAsync(e => e.RecordedAtUtc < DateTimeOffset.UtcNow.AddDays(-90), Token));
        Assert.Equal(1, await db.AgentCredentials.CountAsync(c => c.State == AgentCredentialState.Active, Token));
        Assert.Equal(1, await db.AgentGrants.CountAsync(Token));
    }

    [Fact]
    public async Task Multiple_renewals_preserve_original_connect_latest_poll_and_reject_old_or_changed_sequences()
    {
        await using var fixture = await Fixture.Create();
        var (certificate, _) = await fixture.Enroll();
        var input = await fixture.ConnectInput(certificate);
        var connected = (await fixture.Store(s => s.ConnectAsync(input, Token))).Receipt!;
        PollAgent? first = null;
        PollAgent? latest = null;
        AgentPollReceipt? receipt = null;
        for (var i = 1; i <= 3; i++)
        {
            latest = new(certificate, connected.SessionId, connected.Fence, connected.AuthorizationVersion,
                Guid.NewGuid(), i, AgentReadiness.ReadyForControlPlane, true, 0);
            first ??= latest;
            receipt = (await fixture.Store(s => s.PollAsync(latest, Token))).Receipt!;
            Assert.Equal(connected, (await fixture.Store(s => s.ConnectAsync(input, Token))).Receipt);
        }
        Assert.Equal(receipt, (await fixture.Store(s => s.PollAsync(latest!, Token))).Receipt);
        Assert.Equal(AgentStoreError.StaleReceipt, (await Capture(() => fixture.Store(s => s.PollAsync(first!, Token)))).Error);
        Assert.Equal(AgentStoreError.Conflict, (await Capture(() => fixture.Store(s => s.PollAsync(latest! with { Readiness = AgentReadiness.Unknown }, Token)))).Error);
        Assert.Equal(AgentStoreError.Conflict, (await Capture(() => fixture.Store(s => s.PollAsync(latest! with { RequestId = Guid.NewGuid() }, Token)))).Error);
        Assert.Equal(AgentStoreError.ContextChanged, (await Capture(() => fixture.Store(s => s.PollAsync(latest! with { RequestId = Guid.NewGuid(), Sequence = 5 }, Token)))).Error);
        Assert.Equal(receipt!.LeaseExpiresAtUtc, (await fixture.Store(s => s.ReadSessionAsync(certificate, Token)))!.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task Agent_deletion_guard_preserves_populated_published_inventory_and_owned_encrypted_secret()
    {
        await using var fixture = await Fixture.Create(managed: true);
        await fixture.Enroll();
        await using var scope = fixture.Services.CreateAsyncScope();
        var inventory = scope.ServiceProvider.GetRequiredService<FlowPlane.Application.Providers.DiscoveryInventory.IProviderDiscoveryInventoryStore>();
        await using var refresh = await inventory.BeginRefreshAsync(fixture.ProviderId, Token);
        await refresh.Sink.AppendAsync(ProviderDiscoveryInventoryTests.Observation(fixture.ProviderId, "retained-identity"), Token);
        await refresh.PublishAsync(ProviderDiscoveryInventoryTests.Summary(1), Token);
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var ciphertext = (await db.SecretRecords.AsNoTracking().SingleAsync(Token)).Ciphertext;
        var before = await db.Database.SqlQueryRaw<string>("SELECT row_to_json(t)::text AS \"Value\" FROM \"ProviderDiscoveryStates\" t").SingleAsync(Token);
        var providers = scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>();
        Assert.Equal(AgentStoreError.Conflict, (await Assert.ThrowsAsync<AgentStoreException>(() => providers.DeleteAsync(fixture.ProviderId, 1, Token))).Code);
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>("SELECT row_to_json(t)::text AS \"Value\" FROM \"ProviderDiscoveryStates\" t").SingleAsync(Token));
        Assert.Equal(refresh.RefreshId, (await db.DiscoveryStates.AsNoTracking().SingleAsync(Token)).CurrentGenerationId);
        Assert.Equal(1, await db.DiscoveryObservations.CountAsync(Token));
        Assert.Equal(ciphertext, (await db.SecretRecords.AsNoTracking().SingleAsync(Token)).Ciphertext);
    }

    [Fact]
    public async Task Rollback_guard_holds_locks_until_DDL_and_blocks_a_new_enrollment()
    {
        var boundary = new RollbackBoundary();
        await using var fixture = await Fixture.Create(boundary);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var rollback = db.GetService<IMigrator>().MigrateAsync(Previous, Token);
        Task<(AgentGrantReceipt? Value, AgentStoreError? Error)>? admission = null;
        try
        {
            await boundary.Checked.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            admission = Capture(() => fixture.Store(s => s.IssueGrantAsync(new(new(fixture.ProviderId, 1), Actor, Guid.NewGuid(), "Synthetic", Hash), Token)));
            var blocked = false;
            await using var observerScope = fixture.Services.CreateAsyncScope();
            var observer = observerScope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
            for (var i = 0; i < 100 && !admission.IsCompleted; i++)
            {
                blocked = await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE relation='\"ProviderInstances\"'::regclass AND NOT granted) AS \"Value\"").SingleAsync(Token);
                if (blocked) break;
                await Task.Delay(20, Token);
            }
            Assert.True(blocked);
            Assert.False(admission.IsCompleted);
        }
        finally { boundary.Release.TrySetResult(); await rollback; }
        Assert.Equal(AgentStoreError.Unavailable, (await admission!).Error);
        Assert.Equal(4, (await db.Database.GetAppliedMigrationsAsync(Token)).Count());
    }

    [Fact]
    public async Task Retention_expiry_does_not_erase_unresolved_recovery_but_settled_rollback_preserves_encrypted_data()
    {
        await using var fixture = await Fixture.Create(managed: true);
        var grant = await fixture.Store(s => s.IssueGrantAsync(new(new(fixture.ProviderId, 1), Actor, Guid.NewGuid(), "Synthetic", Hash), Token));
        await fixture.Store(async s => { await s.RevokeAsync(new(grant.AgentId, Actor, 1), Token); return true; });
        await fixture.Sql($"UPDATE \"AgentEnrollmentGrants\" SET \"CreatedAtUtc\"=statement_timestamp()-interval '2 days', \"ExpiresAtUtc\"=statement_timestamp()-interval '2 days'+interval '10 minutes' WHERE \"Id\"={grant.GrantId}");
        await fixture.Sql($"UPDATE \"AgentSecurityEvents\" SET \"RecordedAtUtc\"=clock_timestamp()-interval '91 days' WHERE \"AgentId\"={grant.AgentId}");
        await fixture.Sql($"UPDATE \"Agents\" SET \"RecoveryRequired\"=true WHERE \"Id\"={grant.AgentId}");
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var ciphertext = (await db.SecretRecords.AsNoTracking().SingleAsync(Token)).Ciphertext;
        var keyFingerprint = await db.Database.SqlQueryRaw<string>("SELECT encode(sha256(convert_to(string_agg(\"Xml\",'' ORDER BY \"Id\"),'UTF8')),'hex') AS \"Value\" FROM \"DataProtectionKeys\"").SingleAsync(Token);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(Previous, Token));
        Assert.Equal(PostgresErrorCodes.RaiseException, failure.SqlState);
        Assert.Equal(5, (await db.Database.GetAppliedMigrationsAsync(Token)).Count());
        Assert.True((await db.Agents.AsNoTracking().SingleAsync(Token)).RecoveryRequired);
        await fixture.Sql($"UPDATE \"Agents\" SET \"RecoveryRequired\"=false WHERE \"Id\"={grant.AgentId}");
        await db.GetService<IMigrator>().MigrateAsync(Previous, Token);
        Assert.Equal(4, (await db.Database.GetAppliedMigrationsAsync(Token)).Count());
        Assert.Equal(ciphertext, (await db.SecretRecords.AsNoTracking().SingleAsync(Token)).Ciphertext);
        Assert.Equal(keyFingerprint, await db.Database.SqlQueryRaw<string>("SELECT encode(sha256(convert_to(string_agg(\"Xml\",'' ORDER BY \"Id\"),'UTF8')),'hex') AS \"Value\" FROM \"DataProtectionKeys\"").SingleAsync(Token));
        Assert.True(await db.ProviderInstances.AnyAsync(p => p.Id == fixture.ProviderId, Token));
    }

    [Fact]
    public async Task Terminal_grant_cleanup_is_bounded_and_does_not_delete_the_current_live_grant()
    {
        await using var fixture = await Fixture.Create();
        var issued = await fixture.Store(s => s.IssueGrantAsync(new(new(fixture.ProviderId, 1), Actor, Guid.NewGuid(), "Synthetic", Hash), Token));
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        var original = await db.AgentGrants.AsNoTracking().SingleAsync(Token);
        db.AgentGrants.AddRange(Enumerable.Range(0, 120).Select(_ => original with
        {
            Id = Guid.NewGuid(),
            ClientRequestId = Guid.NewGuid(),
            State = AgentGrantState.Cancelled,
            TokenHash = null,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-2),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(9)
        }));
        await db.SaveChangesAsync(Token);
        Assert.Equal(100, await fixture.Store(s => s.CleanupAsync(issued.AgentId, Token)));
        Assert.Equal(21, await db.AgentGrants.CountAsync(Token));
        Assert.Equal(AgentGrantState.Issued, (await db.AgentGrants.AsNoTracking().SingleAsync(g => g.Id == issued.GrantId, Token)).State);
    }

    [Fact]
    public async Task Lost_claim_proof_and_confirmation_acknowledgements_cannot_duplicate_activation()
    {
        var boundary = new LostCommit();
        await using var fixture = await Fixture.Create(boundary);
        var grant = await fixture.Store(s => s.IssueGrantAsync(new(new(fixture.ProviderId, 1), Actor, Guid.NewGuid(), "Synthetic", Hash), Token));
        var claimInput = new ClaimAgentGrant(grant.GrantId, Guid.NewGuid(), Hash, Certificate());
        boundary.Armed = true;
        Assert.Equal(AgentStoreError.CommitUncertain, (await Capture(() => fixture.Store(s => s.ClaimAsync(claimInput, Token)))).Error);
        var claim = await fixture.Store(s => s.ClaimAsync(claimInput, Token));
        var proofInput = new ProveAgentGrant(grant.GrantId, claimInput.ClaimId, Guid.NewGuid(), claim.Nonce!, new(grant.AgentId, claim.CredentialId!.Value, claimInput.CertificateDer));
        boundary.Armed = true;
        Assert.Equal(AgentStoreError.CommitUncertain, (await Capture(() => fixture.Store(s => s.RecordProofAsync(proofInput, Token)))).Error);
        var proof = await fixture.Store(s => s.RecordProofAsync(proofInput, Token));
        var confirmation = new ConfirmAgentGrant(grant.GrantId, Actor, proof.Version, proof.Nonce!);
        boundary.Armed = true;
        Assert.Equal(AgentStoreError.CommitUncertain, (await Capture(() => fixture.Store(s => s.ConfirmAsync(confirmation, Token)))).Error);
        var active = await fixture.Store(s => s.ConfirmAsync(confirmation, Token));
        Assert.Equal(AgentGrantState.Redeemed, active.State);
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>();
        Assert.Equal(1, await db.AgentCredentials.CountAsync(Token));
        Assert.Equal(1, await db.AgentSecurityEvents.CountAsync(e => e.Kind == AgentSecurityEventKind.Activated, Token));
    }

    private static byte[] Certificate()
    {
        using var key = RSA.Create(3072);
        using var certificate = new CertificateRequest("CN=synthetic-agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return certificate.RawData;
    }
    private static async Task<(T? Value, AgentStoreError? Error)> Capture<T>(Func<Task<T>> work)
    {
        try { return (await work(), null); }
        catch (AgentStoreException error) { return (default, error.Code); }
    }

    private sealed class LostCommit : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed) { Armed = false; throw new IOException("Synthetic acknowledgement loss"); }
            return Task.CompletedTask;
        }
    }
    private sealed class RollbackBoundary : DbCommandInterceptor
    {
        public TaskCompletionSource Checked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DO $agent_rollback$", StringComparison.Ordinal))
            { Checked.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }
    private sealed class Fixture(PostgreSqlTestDatabase database, ServiceProvider services, Guid providerId, string? rootDirectory) : IAsyncDisposable
    {
        public PostgreSqlTestDatabase Database => database;
        public ServiceProvider Services => services;
        public Guid ProviderId => providerId;
        public static async Task<Fixture> Create(IInterceptor? boundary = null, bool managed = false)
        {
            var database = await AgentControlPostgreSqlTests.Create();
            var directory = managed ? Path.Combine(Path.GetTempPath(), $"flowplane-agent-f3-{Guid.NewGuid():N}") : null;
            var root = directory is null ? null : PostgreSqlTestDatabase.CreateRootCertificate(directory, "synthetic-root");
            var services = database.CreateServices(root, boundary is null ? null : registrations =>
                registrations.ConfigureDbContext<FlowPlaneDbContext>(options => options.AddInterceptors(boundary)));
            await database.MigrateAsync(services, Token);
            Guid provider;
            if (managed)
            {
                await using var scope = services.CreateAsyncScope();
                provider = (await scope.ServiceProvider.GetRequiredService<IProviderInstanceStore>().CreateWithManagedSecretAsync(
                    Guid.NewGuid(), ProviderType.Parse("fixture"), "Synthetic", true,
                    new SsisProviderConfigurationCodec().Encode(new("fixture", 1433, "synthetic", false)),
                    "synthetic-only".AsMemory(), DateTimeOffset.UtcNow, Token)).Id;
            }
            else provider = await ProviderDiscoveryInventoryTests.CreateInstanceAsync(services, Token);
            return new(database, services, provider, directory);
        }
        public async Task<T> Store<T>(Func<IAgentControlStore, Task<T>> action)
        {
            await using var scope = services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IAgentControlStore>());
        }
        public async Task<(AgentCertificateContext Certificate, ConfirmAgentGrant Confirm)> Enroll()
        {
            var pair = await AgentControlPostgreSqlTests.Proved(services, providerId);
            await Store(s => s.ConfirmAsync(pair.Confirm, Token));
            return pair;
        }
        public async Task<ConnectAgent> ConnectInput(AgentCertificateContext certificate)
        {
            await using var scope = services.CreateAsyncScope();
            var row = await scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>().Agents.AsNoTracking().SingleAsync(a => a.Id == certificate.AgentId, Token);
            return new(certificate, Guid.NewGuid(), Guid.NewGuid(), row.SessionFence, row.AuthorizationVersion, 1, "0.1.0", true, 0);
        }
        public async Task Sql(FormattableString statement)
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<FlowPlaneDbContext>().Database.ExecuteSqlInterpolatedAsync(statement, Token);
        }
        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync(); await database.DisposeAsync();
            if (rootDirectory is not null) Directory.Delete(rootDirectory, recursive: true); // Only this fixture's generated root, never development material.
        }
    }
}
