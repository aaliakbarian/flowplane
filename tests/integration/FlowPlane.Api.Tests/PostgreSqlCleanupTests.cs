using FlowPlane.Persistence.Tests;

namespace FlowPlane.Api.Tests;

public sealed class PostgreSqlCleanupTests
{
    // The destructive boundary is replaced, not the identity verifier: a failed assertion cannot drop a database.
    [Theory]
    [InlineData("oid")]
    [InlineData("owner")]
    [InlineData("marker")]
    [InlineData("missing")]
    [InlineData("name")]
    public async Task Cleanup_never_invokes_drop_when_database_identity_is_uncertain(string changed)
    {
        var expected = new TestDatabaseIdentity("flowplane_agent_0123456789ab_" + new string('a', 32), 123,
            "test-owner", "flowplane-test:0123456789abcdef0123456789abcdef:nonce");
        var actual = changed switch
        {
            "oid" => expected with { Oid = 124 },
            "owner" => expected with { Owner = "another-owner" },
            "marker" => expected with { Marker = "flowplane-test:another-invocation:nonce" },
            "missing" => null,
            _ => expected with { Name = "flowplane" }
        };
        var deleted = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostgreSqlTestDatabase.DropVerifiedAsync(expected, actual,
            () => { deleted = true; return Task.CompletedTask; }));
        Assert.False(deleted);
    }

    [Fact]
    public async Task Cleanup_invokes_drop_once_only_for_the_exact_owned_database()
    {
        var identity = new TestDatabaseIdentity("flowplane_agent_0123456789ab_" + new string('a', 32), 123,
            "test-owner", "flowplane-test:0123456789abcdef0123456789abcdef:nonce");
        var deleted = 0;
        await PostgreSqlTestDatabase.DropVerifiedAsync(identity, identity, () => { deleted++; return Task.CompletedTask; });
        Assert.Equal(1, deleted);
    }
}
