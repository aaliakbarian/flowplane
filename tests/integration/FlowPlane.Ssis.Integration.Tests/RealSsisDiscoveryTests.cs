using System.Text.Json;

using FlowPlane.Application.Providers;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Secrets;
using FlowPlane.Providers.Ssis;
using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Discovery;

using Microsoft.Extensions.DependencyInjection;

namespace FlowPlane.Ssis.Integration.Tests;

[Collection(RealSsisCollection.Name)]
public sealed class RealSsisDiscoveryTests
{
    private const string FixtureFolder = "FlowPlaneA10";
    private const string FixtureProject = "FlowPlaneA11Fixture";
    private const string SensitiveCanary = "flowplane-a11-sensitive-canary-value";

    [Fact]
    public async Task Least_privilege_principal_discovers_only_the_A11_entry_point_with_safe_metadata()
    {
        var settings = RequiredSettings("VALID");
        var scan = await ScanAsync(settings, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderDiscoveryOutcome.Succeeded, scan.Summary.Outcome);
        Assert.True(scan.Summary.ScanCompleted);
        var entry = Assert.Single(scan.FixtureObservations, item => IsPackage(item, "EntryPoint.dtsx"));
        Assert.NotNull(entry.ExecutableCandidate);
        var inner = Assert.Single(scan.FixtureObservations, item => IsPackage(item, "InternalStep.dtsx"));
        Assert.Null(inner.ExecutableCandidate);
        using var innerJson = JsonDocument.Parse(inner.Metadata.CanonicalJson);
        Assert.False(innerJson.RootElement.GetProperty("isEntryPoint").GetBoolean());
        AssertFixtureMetadata(scan.FixtureObservations);
        AssertNoSensitiveOutput(scan, settings);
    }

    [Fact]
    public async Task Readonly_principal_can_discover_without_execute_permission()
    {
        var settings = RequiredSettings("READONLY");
        var scan = await ScanAsync(settings, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderDiscoveryOutcome.Succeeded, scan.Summary.Outcome);
        Assert.True(scan.Summary.ScanCompleted);
        Assert.NotNull(Assert.Single(scan.FixtureObservations,
            item => IsPackage(item, "EntryPoint.dtsx")).ExecutableCandidate);
        Assert.Null(Assert.Single(scan.FixtureObservations,
            item => IsPackage(item, "InternalStep.dtsx")).ExecutableCandidate);
        AssertFixtureMetadata(scan.FixtureObservations);
        AssertNoSensitiveOutput(scan, settings);
    }

    [Fact]
    public async Task Principal_without_visible_objects_receives_a_successful_empty_result()
    {
        var settings = RequiredSettings("NO_OBJECT_PERMISSION");
        var scan = await ScanAsync(settings, TestContext.Current.CancellationToken);
        Assert.Equal(ProviderDiscoveryOutcome.Succeeded, scan.Summary.Outcome);
        Assert.True(scan.Summary.ScanCompleted);
        Assert.Equal(0, scan.Summary.ObservationsDelivered);
        Assert.Equal(0, scan.Summary.ExecutableCandidates);
        Assert.Empty(scan.Summary.Issues);
        AssertNoSensitiveOutput(scan, settings);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_without_disclosure()
    {
        var settings = RequiredSettings("VALID");
        const string variable = "FLOWPLANE_TEST_SSIS_A11_RUNTIME_WRONG_PASSWORD";
        var canary = Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, canary);
        try
        {
            var scan = await ScanAsync(settings with { PasswordVariable = variable },
                TestContext.Current.CancellationToken);
            Assert.Equal(ProviderDiscoveryOutcome.Failed, scan.Summary.Outcome);
            Assert.False(scan.Summary.ScanCompleted);
            Assert.Contains(scan.Summary.Issues, issue =>
                issue.Code == "ssis.discovery.authentication-rejected");
            AssertSensitiveTextAbsent(scan, canary, "wrong-password canary");
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task Principal_without_SSISDB_access_reports_catalog_inaccessible()
    {
        var settings = RequiredSettings("NO_CATALOG_ACCESS");
        var scan = await ScanAsync(settings, TestContext.Current.CancellationToken);
        Assert.Equal(ProviderDiscoveryOutcome.Failed, scan.Summary.Outcome);
        Assert.False(scan.Summary.ScanCompleted);
        Assert.Contains(scan.Summary.Issues, issue =>
            issue.Code is "ssis.discovery.catalog-inaccessible" or "ssis.discovery.catalog-missing");
        Assert.Equal(0, scan.Summary.ObservationsDelivered);
        AssertNoSensitiveOutput(scan, settings);
    }

    [Fact]
    public async Task Untrusted_certificate_is_rejected_without_downgrade()
    {
        var settings = RequiredSettings("TLS_FAILURE") with { TrustServerCertificate = false };
        var scan = await ScanAsync(settings, TestContext.Current.CancellationToken);
        Assert.Equal(ProviderDiscoveryOutcome.Failed, scan.Summary.Outcome);
        Assert.False(scan.Summary.ScanCompleted);
        Assert.Contains(scan.Summary.Issues, issue =>
            issue.Code == "ssis.discovery.tls-certificate-rejected");
        Assert.Equal(0, scan.Summary.ObservationsDelivered);
        AssertNoSensitiveOutput(scan, settings);
    }

    private static RealSsisSettings RequiredSettings(string role)
    {
        Assert.SkipUnless(bool.TryParse(Environment.GetEnvironmentVariable(
            "FLOWPLANE_TEST_SSIS_A11_ENABLED"), out var enabled) && enabled,
            "A-11 acceptance is Blocked until the separately approved fixture deployment and setup.");
        var settings = RealSsisSettings.Load(role);
        Assert.SkipWhen(settings is null, RealSsisSettings.SkipMessage(role));
        return settings!;
    }

    internal sealed record ScanCapture(ProviderDiscoveryScanResult Summary,
        IReadOnlyList<ProviderDiscoveryObservation> FixtureObservations);

    internal static async Task<ScanCapture> ScanAsync(RealSsisSettings settings,
        CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretResolver, EnvironmentSecretResolver>();
        services.AddFlowPlaneSsisValidation(isDevelopment: true,
            discoveryOptions: new SsisDiscoveryOptions { MaxProjectsPerBatch = 1 });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var discovery = Assert.Single(scope.ServiceProvider.GetServices<IProviderDiscoveryCapability>());
        var codec = new SsisProviderConfigurationCodec();
        var instance = ProviderInstance.Create(
            Guid.Parse("e69e916e-70a7-46df-9ec8-5d3b6feff50f"),
            SsisProviderConfigurationCodec.SsisProviderType, "Controlled discovery scan", true,
            codec.Encode(new SsisProviderConfiguration(settings.Host, settings.Port,
                settings.Username, settings.TrustServerCertificate)),
            SecretReference.Environment(settings.PasswordVariable), DateTimeOffset.UtcNow);
        var sink = new FixtureSink(settings);
        var summary = await discovery.ScanAsync(instance, sink, cancellationToken);
        var capture = new ScanCapture(summary, sink.Observations);
        AssertNoSensitiveOutput(capture, settings);
        return capture;
    }

    private sealed class FixtureSink(RealSsisSettings settings) : IProviderDiscoverySink
    {
        // The test retains only this small fixture; every other catalog observation is checked then released.
        public List<ProviderDiscoveryObservation> Observations { get; } = [];
        public ValueTask AppendAsync(ProviderDiscoveryObservation observation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            AssertSensitiveTextAbsent(observation, SensitiveCanary, "fixture sensitive canary");
            AssertSensitiveTextAbsent(observation,
                Environment.GetEnvironmentVariable(settings.PasswordVariable)!, "SQL password");
            AssertSensitiveTextAbsent(observation, settings.PasswordVariable, "secret locator");
            using var document = JsonDocument.Parse(observation.Metadata.CanonicalJson);
            var root = document.RootElement;
            if (root.TryGetProperty("folderName", out var folder) &&
                folder.GetString() == FixtureFolder &&
                root.TryGetProperty("projectName", out var project) &&
                project.GetString() == FixtureProject)
                Observations.Add(observation);
            return ValueTask.CompletedTask;
        }
    }

    private static bool IsPackage(ProviderDiscoveryObservation item, string package)
    {
        if (item.Metadata.SchemaId != "flowplane.ssis/discovery-package.v1") return false;
        using var document = JsonDocument.Parse(item.Metadata.CanonicalJson);
        return document.RootElement.GetProperty("packageName").GetString() == package;
    }

    private static void AssertFixtureMetadata(IReadOnlyList<ProviderDiscoveryObservation> observations)
    {
        var project = Assert.Single(observations, item =>
            item.Metadata.SchemaId == "flowplane.ssis/discovery-project.v1");
        using (var json = JsonDocument.Parse(project.Metadata.CanonicalJson))
        {
            Assert.Equal(1, json.RootElement.GetProperty("projectFormatVersion").GetInt32());
            Assert.True(json.RootElement.GetProperty("activeObjectVersionLsn").GetInt64() > 0);
        }
        var entry = Assert.Single(observations, item => IsPackage(item, "EntryPoint.dtsx"));
        Assert.Equal("flowplane.ssis/native-definition-reference.v1", entry.ExecutableCandidate!.SchemaId);
        using (var json = JsonDocument.Parse(entry.Metadata.CanonicalJson))
            Assert.Equal(8, json.RootElement.GetProperty("packageFormatVersion").GetInt32());

        var parameters = observations.Where(item =>
            item.Metadata.SchemaId == "flowplane.ssis/discovery-parameter.v1").ToArray();
        Assert.Contains(parameters, item => ParameterMatches(item, "project", "RequiredProjectInput",
            required: true, sensitive: false));
        Assert.Contains(parameters, item => ParameterMatches(item, "project", "SensitiveEnvironmentInput",
            required: true, sensitive: true, environment: true));
        Assert.Contains(parameters, item => ParameterMatches(item, "package", "BatchSize",
            required: false, sensitive: false));
        Assert.Contains(observations, item =>
        {
            if (item.Metadata.SchemaId != "flowplane.ssis/discovery-environment-reference.v1") return false;
            using var document = JsonDocument.Parse(item.Metadata.CanonicalJson);
            var root = document.RootElement;
            return root.GetProperty("referenceType").GetString() == "relative" &&
                root.GetProperty("environmentName").GetString() == "FlowPlaneA11Environment";
        });
    }

    private static bool ParameterMatches(ProviderDiscoveryObservation observation, string scope,
        string name, bool required, bool sensitive, bool environment = false)
    {
        using var json = JsonDocument.Parse(observation.Metadata.CanonicalJson);
        var root = json.RootElement;
        return root.GetProperty("scope").GetString() == scope &&
            root.GetProperty("name").GetString() == name &&
            root.GetProperty("isRequired").GetBoolean() == required &&
            root.GetProperty("isSensitive").GetBoolean() == sensitive &&
            (scope != "package" || root.GetProperty("packageName").GetString() == "EntryPoint.dtsx") &&
            (!environment || (root.GetProperty("valueSource").GetString() == "environmentReference" &&
                root.GetProperty("referencedVariableName").GetString() == "FlowPlaneA11SensitiveValue"));
    }

    private static void AssertNoSensitiveOutput(ScanCapture scan, RealSsisSettings settings)
    {
        AssertSensitiveTextAbsent(scan, SensitiveCanary, "fixture sensitive canary");
        AssertSensitiveTextAbsent(scan, Environment.GetEnvironmentVariable(settings.PasswordVariable)!,
            "SQL password");
        AssertSensitiveTextAbsent(scan, settings.PasswordVariable, "secret locator");
    }

    private static void AssertSensitiveTextAbsent(object value, string secret, string description) =>
        Assert.False(JsonSerializer.Serialize(value).Contains(secret, StringComparison.Ordinal),
            $"Discovery output disclosed the {description}.");
}
