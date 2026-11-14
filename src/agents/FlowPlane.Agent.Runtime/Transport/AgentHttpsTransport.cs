using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FlowPlane.Agent.Protocol;

namespace FlowPlane.Agent.Runtime.Transport;

public enum AgentTransportError { Unavailable, Denied, Conflict, Rejected, InvalidResponse }
public sealed class AgentTransportException(AgentTransportError code) : Exception("The agent HTTPS exchange is unavailable or invalid.")
{
    public AgentTransportError Code { get; } = code;
}

/// <summary>Closed HTTPS-only surface; bootstrap uses a separate handler without a client certificate.</summary>
public sealed class AgentHttpsTransport : IDisposable
{
    private readonly HttpClient bootstrap;
    private readonly HttpClient authenticated;
    public Uri Origin { get; }

    private AgentHttpsTransport(string origin, X509Certificate2 certificate, X509Certificate2? root)
        : this(origin, CreateHandler(null, root), CreateHandler(certificate, root)) { }

    internal AgentHttpsTransport(string origin, HttpMessageHandler bootstrapHandler, HttpMessageHandler authenticatedHandler)
    {
        var address = ValidateOrigin(origin);
        Origin = address;
        bootstrap = new(bootstrapHandler) { BaseAddress = address, Timeout = Timeout.InfiniteTimeSpan };
        authenticated = new(authenticatedHandler) { BaseAddress = address, Timeout = Timeout.InfiniteTimeSpan };
    }

    public static AgentHttpsTransport Create(string origin, X509Certificate2 certificate) => new(origin, certificate, null);

    /// <summary>Explicit approved private issuer or ephemeral test trust only; does not install trust.</summary>
    public static AgentHttpsTransport CreateWithCustomRoot(string origin, X509Certificate2 certificate, X509Certificate2 root) => new(origin, certificate, root);

    public static Uri ValidateOrigin(string origin)
    {
        if (origin is not { Length: > 0 and <= 2048 } || !Uri.TryCreate(origin, UriKind.Absolute, out var address) ||
            address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0 || address.Query.Length != 0 ||
            address.Fragment.Length != 0 || address.AbsolutePath != "/" || address.Host.Length == 0)
            throw new AgentTransportException(AgentTransportError.Rejected);
        return address;
    }

    private static SocketsHttpHandler CreateHandler(X509Certificate2? certificate, X509Certificate2? root)
    {
        if (certificate is not null && (!certificate.HasPrivateKey || !AgentPairingSecurity.IsClientCertificate(certificate.RawData, DateTimeOffset.UtcNow)))
            throw new AgentTransportException(AgentTransportError.Rejected);
        var ssl = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowRenegotiation = false };
        if (certificate is not null) ssl.ClientCertificates = new X509CertificateCollection { certificate };
        if (root is not null)
        {
            ssl.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            ssl.CertificateChainPolicy.CustomTrustStore.Add(root);
            ssl.CertificateChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        }
        // Platform TLS retains SAN/hostname, chain, lifetime and serverAuth validation; no callbacks.
        return new() { AllowAutoRedirect = false, UseCookies = false, SslOptions = ssl, MaxResponseHeadersLength = 16, ConnectTimeout = TimeSpan.FromSeconds(10) };
    }

    public Task<PairingClaimReceipt> ClaimAsync(PairingClaimRequest request, CancellationToken token) => Send<PairingClaimRequest, PairingClaimReceipt>(bootstrap, HttpMethod.Post, "agent/v1/pairing-claims", request, token);
    public Task<PairingClaimReceipt> GetClaimReceiptAsync(Guid claimId, CancellationToken token) => Get<PairingClaimReceipt>($"agent/v1/pairings/{CheckedId(claimId):D}/claim", token);
    public Task<PairingStatusReceipt> PairingProofAsync(PairingProofRequest request, CancellationToken token) => Send<PairingProofRequest, PairingStatusReceipt>(authenticated, HttpMethod.Post, $"agent/v1/pairings/{request.ClaimId:D}/proof", request, token);
    public Task<PairingStatusReceipt> PairingStatusAsync(Guid claimId, CancellationToken token) => Get<PairingStatusReceipt>($"agent/v1/pairings/{CheckedId(claimId):D}", token);
    public Task<AgentSessionStatusReceipt> SessionStatusAsync(CancellationToken token) => Get<AgentSessionStatusReceipt>("agent/v1/session", token);
    public Task<AgentAssignmentsReceipt> GetAssignmentsAsync(CancellationToken token) => Get<AgentAssignmentsReceipt>("agent/v1/assignments", token);
    public Task<AgentSessionReceipt> ConnectAsync(AgentConnectRequest request, CancellationToken token) => Send<AgentConnectRequest, AgentSessionReceipt>(authenticated, HttpMethod.Post, "agent/v1/sessions", request, token);
    public Task<AgentPollReceipt> PollAsync(AgentPollRequest request, CancellationToken token) => Send<AgentPollRequest, AgentPollReceipt>(authenticated, HttpMethod.Post, "agent/v1/polls", request, token);
    public Task<PairingClaimReceipt> RotateAsync(AgentRotationRequest request, CancellationToken token) => Send<AgentRotationRequest, PairingClaimReceipt>(authenticated, HttpMethod.Post, "agent/v1/rotations", request, token);
    public Task<PairingStatusReceipt> RotationProofAsync(Guid grantId, PairingProofRequest request, CancellationToken token) => Send<PairingProofRequest, PairingStatusReceipt>(authenticated, HttpMethod.Post, $"agent/v1/rotations/{CheckedId(grantId):D}/proof", request, token);
    public Task<PairingStatusReceipt> RotationStatusAsync(Guid grantId, CancellationToken token) => Get<PairingStatusReceipt>($"agent/v1/rotations/{CheckedId(grantId):D}", token);
    private Task<T> Get<T>(string path, CancellationToken token) where T : class => Send<object, T>(authenticated, HttpMethod.Get, path, null, token);
    private static Guid CheckedId(Guid id) => id != Guid.Empty ? id : throw new AgentTransportException(AgentTransportError.Rejected);

    private static async Task<TResponse> Send<TRequest, TResponse>(HttpClient client, HttpMethod method, string path, TRequest? payload, CancellationToken token)
        where TRequest : class where TResponse : class
    {
        byte[]? body = null;
        byte[]? reply = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Accept.ParseAdd("application/json");
            if (payload is not null)
            {
                body = AgentProtocolCodec.Encode(payload);
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new("application/json");
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw new AgentTransportException(response.StatusCode switch
            {
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => AgentTransportError.Denied,
                HttpStatusCode.Conflict => AgentTransportError.Conflict,
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests => AgentTransportError.Unavailable,
                _ => AgentTransportError.Rejected,
            });
            if (response.Content.Headers.ContentType?.MediaType != "application/json") throw new AgentTransportException(AgentTransportError.InvalidResponse);
            if (response.Content.Headers.ContentLength is > AgentProtocol.MaxPayloadBytes) throw new AgentTransportException(AgentTransportError.InvalidResponse);
            reply = new byte[AgentProtocol.MaxPayloadBytes + 1];
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            var length = 0;
            while (length < reply.Length)
            {
                var count = await input.ReadAsync(reply.AsMemory(length), deadline.Token);
                if (count == 0) break;
                length += count;
            }
            if (length > AgentProtocol.MaxPayloadBytes) throw new AgentTransportException(AgentTransportError.InvalidResponse);
            return AgentProtocolCodec.Decode<TResponse>(reply.AsSpan(0, length));
        }
        catch (AgentProtocolException) { throw new AgentTransportException(AgentTransportError.InvalidResponse); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or AuthenticationException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            throw new AgentTransportException(AgentTransportError.Unavailable);
        }
        finally
        {
            if (body is not null) CryptographicOperations.ZeroMemory(body);
            if (reply is not null) CryptographicOperations.ZeroMemory(reply);
        }
    }

    public void Dispose() { bootstrap.Dispose(); authenticated.Dispose(); }
}
