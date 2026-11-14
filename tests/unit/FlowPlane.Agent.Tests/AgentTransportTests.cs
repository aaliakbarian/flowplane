using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FlowPlane.Agent.Protocol;
using FlowPlane.Agent.Runtime.Transport;

namespace FlowPlane.Agent.Tests;

public sealed class AgentTransportTests
{
    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("https://user@localhost/")]
    [InlineData("https://localhost/path")]
    [InlineData("https://localhost/?secret=bad")]
    [InlineData("https://localhost/#fragment")]
    public void Untrusted_origin_shapes_fail_before_a_request(string origin) =>
        Assert.Throws<AgentTransportException>(() => AgentHttpsTransport.ValidateOrigin(origin));

    [Fact]
    public async Task Claim_uses_separate_client_and_redirects_are_never_followed()
    {
        var claims = 0;
        var operations = 0;
        using var transport = new AgentHttpsTransport("https://localhost:8443/",
            new Handler((request, _) => { claims++; Assert.Equal("/agent/v1/pairing-claims", request.RequestUri!.AbsolutePath); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://elsewhere/") } }); }),
            new Handler((_, _) => { operations++; throw new InvalidOperationException(); }));
        var failure = await Assert.ThrowsAsync<AgentTransportException>(() => transport.ClaimAsync(new("flowplane.agent/pairing-claim.v1", Guid.NewGuid(), Guid.NewGuid(), new string('A', 43), "AQ=="), TestContext.Current.CancellationToken));
        Assert.Equal(AgentTransportError.Rejected, failure.Code);
        Assert.Equal(1, claims);
        Assert.Equal(0, operations);
    }

    [Fact]
    public async Task Oversize_and_raw_network_failures_are_bounded_and_sanitized()
    {
        using var transport = new AgentHttpsTransport("https://localhost/", new Handler((_, _) => throw new HttpRequestException("canary-secret")),
            new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[65537]) { Headers = { ContentType = new("application/json") } } })));
        var failure = await Assert.ThrowsAsync<AgentTransportException>(() => transport.ClaimAsync(new("flowplane.agent/pairing-claim.v1", Guid.NewGuid(), Guid.NewGuid(), new string('A', 43), "AQ=="), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("canary", failure.ToString());
        Assert.Null(failure.InnerException);
        Assert.Equal(AgentTransportError.Unavailable, failure.Code);
        Assert.Equal(AgentTransportError.InvalidResponse, (await Assert.ThrowsAsync<AgentTransportException>(() => transport.SessionStatusAsync(TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task Valid_wire_json_served_as_html_is_rejected()
    {
        using var transport = new AgentHttpsTransport("https://localhost/", new Handler((_, _) => throw new InvalidOperationException()), new Handler((_, _) =>
        {
            var response = AgentControlFixture.Reply(new AgentSessionStatusReceipt("flowplane.agent/session-status.v1", Guid.NewGuid(), 1, AgentControlFixture.Now, null, AgentRecoveryMode.None));
            response.Content.Headers.ContentType = new("text/html");
            return Task.FromResult(response);
        }));
        var failure = await Assert.ThrowsAsync<AgentTransportException>(() => transport.SessionStatusAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AgentTransportError.InvalidResponse, failure.Code);
    }

    [Fact]
    public async Task Rate_limiting_is_retryable_without_replacing_mutation_identity()
    {
        using var transport = new AgentHttpsTransport("https://localhost/", new Handler((_, _) => throw new InvalidOperationException()),
            new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))));
        var failure = await Assert.ThrowsAsync<AgentTransportException>(() => transport.SessionStatusAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AgentTransportError.Unavailable, failure.Code);
    }

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
