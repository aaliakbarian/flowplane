namespace FlowPlane.Infrastructure.Persistence;

internal sealed class SecretRecord
{
    private SecretRecord()
    {
        Ciphertext = [];
        ProtectionScheme = null!;
        FormatVersion = null!;
    }

    public SecretRecord(Guid id, byte[] ciphertext, DateTimeOffset now)
    {
        Id = id;
        Ciphertext = ciphertext;
        ProtectionScheme = "aspnet-data-protection";
        FormatVersion = "v1";
        Version = 1;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public Guid Id { get; private set; }

    public byte[] Ciphertext { get; private set; }

    public string ProtectionScheme { get; private set; }

    public string FormatVersion { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Replace(byte[] ciphertext, DateTimeOffset now)
    {
        Ciphertext = ciphertext;
        Version++;
        UpdatedAtUtc = now;
    }
}
