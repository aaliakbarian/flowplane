using System.Net.Sockets;
using System.Security.Authentication;

namespace FlowPlane.Providers.Ssis.Validation;

internal enum SsisValidationStage
{
    MasterConnection,
    CatalogConnection,
    CatalogQuery,
}

internal enum SsisFailureKind
{
    DnsResolutionFailed,
    EndpointUnreachable,
    Timeout,
    TlsCertificateRejected,
    AuthenticationRejected,
    CatalogInaccessible,
    PermissionDenied,
    Unexpected,
}

internal sealed record SsisSqlFailure(int? Number, Exception? Exception)
{
    public static SsisSqlFailure FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        int? number = exception is Microsoft.Data.SqlClient.SqlException sqlException
            ? sqlException.Number
            : null;
        return new SsisSqlFailure(number, exception);
    }
}

internal static class SsisSqlFailureClassifier
{
    public static SsisFailureKind Classify(SsisSqlFailure failure, SsisValidationStage stage)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (Find<AuthenticationException>(failure.Exception) is not null)
        {
            return SsisFailureKind.TlsCertificateRejected;
        }

        var socket = Find<SocketException>(failure.Exception);
        if (socket is not null)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                    SsisFailureKind.DnsResolutionFailed,
                SocketError.TimedOut => SsisFailureKind.Timeout,
                _ => SsisFailureKind.EndpointUnreachable,
            };
        }

        return failure.Number switch
        {
            -2 => SsisFailureKind.Timeout,
            18456 when stage == SsisValidationStage.MasterConnection =>
                SsisFailureKind.AuthenticationRejected,
            4060 or 916 when stage != SsisValidationStage.MasterConnection =>
                SsisFailureKind.CatalogInaccessible,
            229 when stage == SsisValidationStage.CatalogQuery =>
                SsisFailureKind.PermissionDenied,
            _ => SsisFailureKind.Unexpected,
        };
    }

    private static TException? Find<TException>(Exception? exception)
        where TException : Exception
    {
        while (exception is not null)
        {
            if (exception is TException match)
            {
                return match;
            }

            exception = exception.InnerException;
        }

        return null;
    }
}
