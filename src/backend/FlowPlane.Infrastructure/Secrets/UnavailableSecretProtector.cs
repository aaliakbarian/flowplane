using FlowPlane.Application.Secrets;

namespace FlowPlane.Infrastructure.Secrets;

internal sealed class UnavailableSecretProtector : ISecretProtector
{
    public byte[] Protect(Guid secretId, ReadOnlySpan<byte> plaintext) => throw CreateError();

    public byte[] Unprotect(Guid secretId, ReadOnlySpan<byte> ciphertext) => throw CreateError();

    private static SecretResolutionException CreateError() => new(
        SecretErrorCode.ProtectionUnavailable,
        "Managed secret protection is unavailable because external root material is not configured.");
}
