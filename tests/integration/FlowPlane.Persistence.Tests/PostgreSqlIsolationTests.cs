namespace FlowPlane.Persistence.Tests;

public sealed class PostgreSqlIsolationTests
{
    [Theory]
    [InlineData("oid")]
    [InlineData("owner")]
    [InlineData("marker")]
    [InlineData("missing")]
    [InlineData("development")]
    public void Cleanup_refuses_changed_unidentified_or_development_database(string change)
    {
        var expected = new TestDatabaseIdentity("flowplane_agent_0123456789ab_" + new string('a', 32), 123, "fixture-owner", "flowplane-test:run:nonce");
        TestDatabaseIdentity? actual = expected;
        actual = change switch
        {
            "oid" => expected with { Oid = 124 },
            "owner" => expected with { Owner = "other" },
            "marker" => expected with { Marker = "other-run" },
            "missing" => null,
            _ => expected with { Name = "flowplane" }
        };
        Assert.Throws<InvalidOperationException>(() => PostgreSqlTestDatabase.VerifyCleanupIdentity(expected, actual));
    }

    [Fact]
    public void Cleanup_accepts_only_the_exact_created_identity()
    {
        var identity = new TestDatabaseIdentity("flowplane_agent_0123456789ab_" + new string('a', 32), 123, "fixture-owner", "flowplane-test:run:nonce");
        PostgreSqlTestDatabase.VerifyCleanupIdentity(identity, identity);
    }
}
