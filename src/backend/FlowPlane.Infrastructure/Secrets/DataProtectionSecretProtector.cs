using System.Security.Cryptography;

using FlowPlane.Application.Secrets;

using Microsoft.AspNetCore.DataProtection;

namespace FlowPlane.Infrastructure.Secrets;

internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private const string ComponentPurpose = "FlowPlane.SecretStore";
    private const string FormatPurpose = "v1";

    public byte[] Protect(Guid secretId, ReadOnlySpan<byte> plaintext)
    {
        try
        {
            return CreateProtector(secretId).Protect(plaintext.ToArray());
        }
        catch (CryptographicException)
        {
            throw SafeFailure();
        }
    }

    public byte[] Unprotect(Guid secretId, ReadOnlySpan<byte> ciphertext)
    {
        try
        {
            return CreateProtector(secretId).Unprotect(ciphertext.ToArray());
        }
        catch (CryptographicException)
        {
            throw SafeFailure();
        }
    }

    private IDataProtector CreateProtector(Guid secretId) => provider.CreateProtector(
        ComponentPurpose,
        FormatPurpose,
        secretId.ToString("D"));

    private static SecretResolutionException SafeFailure() => new(
        SecretErrorCode.ProtectionFailed,
        "The managed secret could not be protected or resolved safely.");
}
