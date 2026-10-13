namespace FlowPlane.Infrastructure.Secrets;

internal interface ISecretProtector
{
    byte[] Protect(Guid secretId, ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(Guid secretId, ReadOnlySpan<byte> ciphertext);
}
