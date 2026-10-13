using FlowPlane.Application.Providers;
using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Validation;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisSqlValidationProbeTests
{
    [Fact]
    public async Task Real_executor_classifies_closed_loopback_endpoint_without_secret_disclosure()
    {
        const string canary = "a10-real-executor-password-canary";
        var executor = new SsisSqlExecutor();

        var exception = await Assert.ThrowsAsync<SsisProbeException>(() =>
            executor.ReadMasterFactsAsync(
                new SsisProviderConfiguration("127.0.0.1", 1, "flowplane", false),
                canary.AsMemory(),
                new SsisValidationOptions(1, 1, 3),
                TestContext.Current.CancellationToken));

        Assert.Contains(exception.Kind, new[]
        {
            SsisFailureKind.EndpointUnreachable,
            SsisFailureKind.Timeout,
        });
        Assert.DoesNotContain(canary, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_visible_catalog_keeps_capabilities_unknown_while_version_is_unverified()
    {
        var executor = new StubSqlExecutor(
            MasterFacts("16.0.4135.4", 1),
            new SsisCatalogEvidence(
                new SsisPermissionEvidence(false, false, false, false, false),
                [],
                []));
        var probe = new SsisSqlValidationProbe(executor, SsisValidationOptions.Default);

        var result = await probe.ValidateAsync(
            Configuration(),
            "secret".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProviderValidationOutcome.Failed, result.Outcome);
        Assert.Contains(result.Checks, check =>
            check.Code == "ssis.server-version.unverified" &&
            check.Status == ProviderValidationCheckStatus.Failed);
        Assert.All(result.Capabilities, capability =>
            Assert.Equal(ProviderCapabilityAvailability.Unknown, capability.Availability));
        Assert.Contains(result.Checks, check =>
            check.Code == "ssis.catalog.surface-compatible" &&
            check.Status == ProviderValidationCheckStatus.Succeeded);
    }

    [Fact]
    public async Task Recognized_unverified_server_still_runs_catalog_and_capability_probes()
    {
        var executor = new StubSqlExecutor(
            MasterFacts("16.0.4135.4", 1),
            new SsisCatalogEvidence(
                new SsisPermissionEvidence(true, true, true, false, false),
                [16],
                [16]));
        var probe = new SsisSqlValidationProbe(executor, SsisValidationOptions.Default);

        var result = await probe.ValidateAsync(
            Configuration(),
            "secret".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProviderValidationOutcome.Failed, result.Outcome);
        Assert.Contains(result.Checks, check =>
            check.Code == "ssis.server-version.unverified" &&
            check.Status == ProviderValidationCheckStatus.Failed);
        Assert.Contains(result.Checks, check =>
            check.Code == "ssis.catalog.surface-compatible" &&
            check.Status == ProviderValidationCheckStatus.Succeeded);
        Assert.All(result.Capabilities, capability =>
            Assert.Equal(ProviderCapabilityAvailability.Available, capability.Availability));
        Assert.Equal(1, executor.CatalogCallCount);
    }

    [Fact]
    public async Task Unsupported_server_major_fails_overall_validation()
    {
        var executor = new StubSqlExecutor(MasterFacts("14.0.1000.1", 1), EmptyEvidence());
        var probe = new SsisSqlValidationProbe(executor, SsisValidationOptions.Default);

        var result = await probe.ValidateAsync(
            Configuration(),
            "secret".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProviderValidationOutcome.Failed, result.Outcome);
        Assert.Contains(result.Checks, check =>
            check.Code == "ssis.server-version.unsupported" &&
            check.Status == ProviderValidationCheckStatus.Failed);
        Assert.Single(result.Checks, check => check.Code.StartsWith(
            "ssis.server-version.",
            StringComparison.Ordinal));
        Assert.Equal(0, executor.CatalogCallCount);
    }

    [Theory]
    [InlineData(null, "ssis.catalog.missing")]
    [InlineData(0, "ssis.catalog.inaccessible")]
    public async Task HasDbAccess_failure_stops_before_catalog_connection(
        int? hasDbAccess,
        string expectedCode)
    {
        var executor = new StubSqlExecutor(MasterFacts("16.0.4135.4", hasDbAccess), EmptyEvidence());
        var probe = new SsisSqlValidationProbe(executor, SsisValidationOptions.Default);

        var result = await probe.ValidateAsync(
            Configuration(),
            "secret".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProviderValidationOutcome.Failed, result.Outcome);
        Assert.Contains(result.Checks, check => check.Code == expectedCode);
        Assert.Single(result.Checks, check => check.Code.StartsWith(
            "ssis.catalog.",
            StringComparison.Ordinal) && check.Code != "ssis.catalog.surface-compatible");
        Assert.Equal(0, executor.CatalogCallCount);
    }

    [Fact]
    public async Task Unknown_visible_definition_format_does_not_fail_connectivity_validation()
    {
        var executor = new StubSqlExecutor(
            MasterFacts("16.0.4135.4", 1),
            new SsisCatalogEvidence(
                new SsisPermissionEvidence(true, true, true, false, false),
                [999],
                [16]));
        var probe = new SsisSqlValidationProbe(executor, SsisValidationOptions.Default);

        var result = await probe.ValidateAsync(
            Configuration(),
            "secret".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProviderValidationOutcome.Failed, result.Outcome);
        Assert.Contains(result.Checks, check => check.Code == "ssis.server-version.unverified");
        Assert.Contains(result.Checks, check =>
            check.Code == "ssis.definition-format.unsupported" &&
            check.Status == ProviderValidationCheckStatus.Failed);
        Assert.All(result.Capabilities, capability =>
            Assert.Equal(ProviderCapabilityAvailability.Unknown, capability.Availability));
        Assert.Contains(result.Facts, fact =>
            fact.Code == "ssis.project-format.observed" && fact.Value == "999");
    }

    [Theory]
    [InlineData(0, "ssis.endpoint.dns-failed")]
    [InlineData(1, "ssis.endpoint.unreachable")]
    [InlineData(2, "ssis.endpoint.timeout")]
    [InlineData(3, "ssis.tls.certificate-rejected")]
    [InlineData(4, "ssis.authentication.rejected")]
    public async Task Master_connection_failure_maps_to_sanitized_staged_check(
        int failureKind,
        string expectedCode)
    {
        var executor = new ThrowingSqlExecutor(
            new SsisProbeException(
                (SsisFailureKind)failureKind,
                SsisValidationStage.MasterConnection));
        var probe = new SsisSqlValidationProbe(executor, SsisValidationOptions.Default);

        var result = await probe.ValidateAsync(
            Configuration(),
            "secret-canary".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(ProviderValidationOutcome.Succeeded, result.Outcome);
        Assert.Contains(result.Checks, check => check.Code == expectedCode);
        Assert.DoesNotContain("secret-canary", result.ToString(), StringComparison.Ordinal);
    }

    private static SsisProviderConfiguration Configuration() => new(
        "sql.example.internal",
        1433,
        "flowplane_runtime",
        TrustServerCertificate: false);

    private static SsisMasterFacts MasterFacts(string version, int? hasDbAccess) =>
        new(version, "CU", "Developer Edition", hasDbAccess);

    private static SsisCatalogEvidence EmptyEvidence() => new(
        new SsisPermissionEvidence(false, false, false, false, false),
        [],
        []);

    private sealed class StubSqlExecutor(
        SsisMasterFacts masterFacts,
        SsisCatalogEvidence catalogEvidence) : ISsisSqlExecutor
    {
        public int CatalogCallCount { get; private set; }

        public Task<SsisMasterFacts> ReadMasterFactsAsync(
            SsisProviderConfiguration configuration,
            ReadOnlyMemory<char> password,
            SsisValidationOptions options,
            CancellationToken cancellationToken) => Task.FromResult(masterFacts);

        public Task<SsisCatalogEvidence> ReadCatalogEvidenceAsync(
            SsisProviderConfiguration configuration,
            ReadOnlyMemory<char> password,
            SsisValidationOptions options,
            CancellationToken cancellationToken)
        {
            CatalogCallCount++;
            return Task.FromResult(catalogEvidence);
        }
    }

    private sealed class ThrowingSqlExecutor(SsisProbeException exception) : ISsisSqlExecutor
    {
        public Task<SsisMasterFacts> ReadMasterFactsAsync(
            SsisProviderConfiguration configuration,
            ReadOnlyMemory<char> password,
            SsisValidationOptions options,
            CancellationToken cancellationToken) => Task.FromException<SsisMasterFacts>(exception);

        public Task<SsisCatalogEvidence> ReadCatalogEvidenceAsync(
            SsisProviderConfiguration configuration,
            ReadOnlyMemory<char> password,
            SsisValidationOptions options,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Catalog probing must not run.");
    }
}
