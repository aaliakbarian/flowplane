using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FlowPlane.Agent.Protocol;

public static class AgentCertificateValidation
{
    public static bool IsValid(ReadOnlyMemory<byte> der, DateTimeOffset now)
    {
        if (der.IsEmpty || der.Length > 16384 || now.Offset != TimeSpan.Zero) return false;
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der.Span);
            using var key = certificate.GetRSAPublicKey();
            if (key is null || key.KeySize != 3072 || certificate.SignatureAlgorithm.Value != "1.2.840.113549.1.1.11" ||
                !certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData) ||
                now.UtcDateTime < certificate.NotBefore.ToUniversalTime() || now.UtcDateTime >= certificate.NotAfter.ToUniversalTime() ||
                certificate.Extensions.Count != 3 ||
                certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault() is not { CertificateAuthority: false, Critical: true } ||
                certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault() is not { KeyUsages: X509KeyUsageFlags.DigitalSignature, Critical: true } ||
                certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault() is not { Critical: true } eku ||
                eku.EnhancedKeyUsages.Count != 1 || eku.EnhancedKeyUsages[0].Value != "1.3.6.1.5.5.7.3.2") return false;
            var reader = new AsnReader(der, AsnEncodingRules.DER);
            var outer = reader.ReadSequence();
            var tbs = outer.ReadEncodedValue();
            var algorithm = outer.ReadSequence();
            if (algorithm.ReadObjectIdentifier() != "1.2.840.113549.1.1.11") return false;
            algorithm.ReadNull(); algorithm.ThrowIfNotEmpty();
            var signature = outer.ReadBitString(out var unused);
            outer.ThrowIfNotEmpty(); reader.ThrowIfNotEmpty();
            var tbsReader = new AsnReader(tbs, AsnEncodingRules.DER).ReadSequence();
            var version = tbsReader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
            if (version.ReadInteger() != 2) return false;
            version.ThrowIfNotEmpty();
            if (tbsReader.ReadInteger() <= 0) return false;
            var innerAlgorithm = tbsReader.ReadSequence();
            if (innerAlgorithm.ReadObjectIdentifier() != "1.2.840.113549.1.1.11") return false;
            innerAlgorithm.ReadNull(); innerAlgorithm.ThrowIfNotEmpty();
            return unused == 0 && key.VerifyData(tbs.Span, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception exception) when (exception is CryptographicException or AsnContentException or InvalidOperationException or ArgumentException)
        { return false; }
    }
}
