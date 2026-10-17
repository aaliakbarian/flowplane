using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Validation;

using Microsoft.Data.SqlClient;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisValidationPolicyTests
{
    [Fact]
    public void Connection_settings_require_encryption_certificate_validation_and_disable_pooling()
    {
        const string canary = "a10-connection-password-canary";
        var configuration = new SsisProviderConfiguration(
            "sql.example.internal",
            1433,
            "flowplane_runtime",
            TrustServerCertificate: false);

        var builder = SsisConnectionSettingsFactory.Create(
            configuration,
            canary.AsMemory(),
            "master",
            SsisValidationOptions.Default);

        Assert.Equal("tcp:sql.example.internal,1433", builder.DataSource);
        Assert.Equal("master", builder.InitialCatalog);
        Assert.Equal("flowplane_runtime", builder.UserID);
        Assert.Equal(canary, builder.Password);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
        Assert.False(builder.Pooling);
        Assert.False(builder.PersistSecurityInfo);
        Assert.Equal(5, builder.ConnectTimeout);
        Assert.Equal("FlowPlane", builder.ApplicationName);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public void Certificate_trust_exception_requires_explicit_configuration_and_development(
        bool trustServerCertificate,
        bool isDevelopment,
        bool expected)
    {
        var configuration = new SsisProviderConfiguration(
            "sql.example.internal",
            1433,
            "flowplane_runtime",
            trustServerCertificate);

        Assert.Equal(
            expected,
            new SsisValidationSecurityPolicy(isDevelopment).Allows(configuration));
    }

    [Theory]
    [InlineData("15.0.2000.5", "Unverified")]
    [InlineData("16.0.4135.4", "Unverified")]
    [InlineData("17.0.1000.1", "Unverified")]
    [InlineData("14.0.1000.1", "Unsupported")]
    [InlineData("not-a-version", "Unsupported")]
    public void Server_version_policy_distinguishes_recognized_targets_from_supported_versions(
        string productVersion,
        string expected)
    {
        Assert.Equal(
            expected,
            SsisCompatibilityPolicy.EvaluateServerVersion(productVersion).ToString());
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    public void HasDbAccess_result_is_mapped_conservatively(int? value, int expected)
    {
        Assert.Equal((SsisCatalogAccess)expected, SsisCatalogAccessMapper.Map(value));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(8, true)]
    [InlineData(999, true)]
    public void Project_format_policy_recognizes_only_the_observed_project_format(
        int format,
        bool expectedUnknown)
    {
        Assert.Equal(
            expectedUnknown,
            SsisCompatibilityPolicy.HasUnknownProjectFormat([format]));
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(1, true)]
    [InlineData(999, true)]
    public void Package_format_policy_recognizes_only_the_observed_package_format(
        int format,
        bool expectedUnknown)
    {
        Assert.Equal(
            expectedUnknown,
            SsisCompatibilityPolicy.HasUnknownPackageFormat([format]));
    }

    [Theory]
    [InlineData(0, 10, 30)]
    [InlineData(5, 0, 30)]
    [InlineData(5, 10, 0)]
    public void Timeout_options_reject_nonpositive_values(
        int connectionTimeoutSeconds,
        int commandTimeoutSeconds,
        int overallTimeoutSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SsisValidationOptions(
            connectionTimeoutSeconds,
            commandTimeoutSeconds,
            overallTimeoutSeconds));
    }
}
