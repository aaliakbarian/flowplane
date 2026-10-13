namespace FlowPlane.Application.Secrets;

public enum SecretErrorCode
{
    UnsupportedReference,
    SecretNotFound,
    ProtectionUnavailable,
    ProtectionFailed,
    ConcurrencyConflict,
}

public sealed class SecretResolutionException : Exception
{
    public SecretResolutionException(SecretErrorCode code, string safeMessage)
        : base(safeMessage)
    {
        Code = code;
    }

    public SecretErrorCode Code { get; }
}
