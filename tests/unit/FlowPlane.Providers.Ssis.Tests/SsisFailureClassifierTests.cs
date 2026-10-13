using System.Net.Sockets;
using System.Security.Authentication;

using FlowPlane.Providers.Ssis.Validation;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisFailureClassifierTests
{
    [Theory]
    [InlineData(18456, 0, 4)]
    [InlineData(4060, 1, 5)]
    [InlineData(916, 1, 5)]
    [InlineData(229, 2, 6)]
    [InlineData(-2, 0, 2)]
    public void Documented_sql_numbers_map_without_native_message_text(
        int number,
        int stage,
        int expected)
    {
        var failure = new SsisSqlFailure(number, null);

        Assert.Equal(
            (SsisFailureKind)expected,
            SsisSqlFailureClassifier.Classify(failure, (SsisValidationStage)stage));
    }

    [Fact]
    public void Certificate_authentication_failure_maps_to_tls()
    {
        var failure = new SsisSqlFailure(
            null,
            new AuthenticationException("native text must not escape"));

        Assert.Equal(
            SsisFailureKind.TlsCertificateRejected,
            SsisSqlFailureClassifier.Classify(failure, SsisValidationStage.MasterConnection));
    }

    [Theory]
    [InlineData(SocketError.HostNotFound, 0)]
    [InlineData(SocketError.NoData, 0)]
    [InlineData(SocketError.TimedOut, 2)]
    [InlineData(SocketError.ConnectionRefused, 1)]
    [InlineData(SocketError.NetworkUnreachable, 1)]
    public void Socket_failures_map_to_bounded_transport_categories(
        SocketError socketError,
        int expected)
    {
        var failure = new SsisSqlFailure(null, new SocketException((int)socketError));

        Assert.Equal(
            (SsisFailureKind)expected,
            SsisSqlFailureClassifier.Classify(failure, SsisValidationStage.MasterConnection));
    }

    [Fact]
    public void Unknown_failure_maps_to_sanitized_unexpected_category()
    {
        var failure = new SsisSqlFailure(50000, new InvalidOperationException("sensitive native text"));

        Assert.Equal(
            SsisFailureKind.Unexpected,
            SsisSqlFailureClassifier.Classify(failure, SsisValidationStage.CatalogQuery));
    }
}
