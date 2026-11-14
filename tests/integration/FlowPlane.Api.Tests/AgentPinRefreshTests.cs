using System.Security.Cryptography;
using FlowPlane.Api.Agents;
using FlowPlane.Application.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlowPlane.Api.Tests;

public sealed class AgentPinRefreshTests
{
    [Fact]
    public async Task Cancelled_waiter_keeps_older_snapshot_closed_until_a_new_complete_refresh()
    {
        using var rsa = RSA.Create(3072);
        using var certificate = AgentBoundaryTests.Certificate(rsa, "cancelled-refresh");
        using var added = AgentBoundaryTests.Certificate(rsa, "committed-before-cancellation");
        var pins = new AgentPinRegistry();
        var health = new AgentPinHealthCheck(new AgentControlOptions { Enabled = true }, pins);
        var recoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var reader = DispatchProxyReader.Create(async token =>
        {
            if (Interlocked.Increment(ref reads) == 1) return [new(Guid.NewGuid(), Guid.NewGuid(), certificate.RawData)];
            recoveryEntered.TrySetResult();
            await releaseRecovery.Task.WaitAsync(token);
            return [new(Guid.NewGuid(), Guid.NewGuid(), certificate.RawData), new(Guid.NewGuid(), Guid.NewGuid(), added.RawData)];
        });
        using var services = new ServiceCollection().AddSingleton(reader).BuildServiceProvider();
        using var loader = new AgentPinLoader(services.GetRequiredService<IServiceScopeFactory>(), pins);
        await loader.StartAsync(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var older = pins.RefreshAsync(async _ => { entered.SetResult(); await release.Task; return [new(Guid.NewGuid(), Guid.NewGuid(), certificate.RawData)]; }, CancellationToken.None);
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            using var cancelled = new CancellationTokenSource();
            var waiter = pins.RefreshAsync(_ => throw new InvalidOperationException("cancelled-waiter-must-not-read"), cancelled.Token);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
            release.SetResult();
            await older;
            Assert.True(pins.NeedsRefresh);
            Assert.False(pins.Accepts(certificate, DateTimeOffset.UtcNow));
            Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync(new(), TestContext.Current.CancellationToken)).Status);
            await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            releaseRecovery.SetResult();
            await WaitReady(pins);
            Assert.True(pins.Accepts(certificate, DateTimeOffset.UtcNow));
            Assert.True(pins.Accepts(added, DateTimeOffset.UtcNow));
            Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new(), TestContext.Current.CancellationToken)).Status);
        }
        finally { release.TrySetResult(); releaseRecovery.TrySetResult(); await loader.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Theory]
    [InlineData(true, HealthStatus.Unhealthy)]
    [InlineData(false, HealthStatus.Healthy)]
    public async Task Readiness_is_closed_while_enabled_pins_need_refresh(bool enabled, HealthStatus expected)
    {
        var pins = new AgentPinRegistry();
        var health = new AgentPinHealthCheck(new AgentControlOptions { Enabled = enabled }, pins);
        Assert.Equal(expected, (await health.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);
        pins.Replace([]);
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Full_snapshot_read_and_publication_are_serialized_so_an_old_read_cannot_replace_a_new_snapshot()
    {
        using var rsa = RSA.Create(3072);
        using var first = AgentBoundaryTests.Certificate(rsa, "first");
        using var second = AgentBoundaryTests.Certificate(rsa, "second");
        var pins = new AgentPinRegistry();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = false;
        var older = pins.RefreshAsync(async token => { entered.SetResult(); await release.Task.WaitAsync(token); return [new(Guid.NewGuid(), Guid.NewGuid(), first.RawData)]; }, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var newer = pins.RefreshAsync(_ => { secondRead = true; return Task.FromResult<IReadOnlyList<AgentPublicPin>>([new(Guid.NewGuid(), Guid.NewGuid(), first.RawData), new(Guid.NewGuid(), Guid.NewGuid(), second.RawData)]); }, TestContext.Current.CancellationToken);
        try { Assert.False(secondRead); }
        finally { release.SetResult(); }
        await Task.WhenAll(older, newer);
        Assert.True(pins.Accepts(first, DateTimeOffset.UtcNow));
        Assert.True(pins.Accepts(second, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Failed_refresh_closes_pins_and_hosted_loader_recovers_without_operator_mutation_or_restart()
    {
        using var rsa = RSA.Create(3072);
        using var certificate = AgentBoundaryTests.Certificate(rsa, "recovery");
        var pins = new AgentPinRegistry();
        pins.Replace([new(Guid.NewGuid(), Guid.NewGuid(), certificate.RawData)]);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var reader = DispatchProxyReader.Create(async token =>
        {
            await Task.Yield();
            if (Interlocked.Increment(ref reads) > 1) recovered.TrySetResult();
            return [new(Guid.NewGuid(), Guid.NewGuid(), certificate.RawData)];
        });
        using var services = new ServiceCollection().AddSingleton(reader).BuildServiceProvider();
        using var loader = new AgentPinLoader(services.GetRequiredService<IServiceScopeFactory>(), pins);
        await loader.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => pins.RefreshAsync(_ => throw new IOException("synthetic-read-failure"), TestContext.Current.CancellationToken));
            Assert.False(pins.Accepts(certificate, DateTimeOffset.UtcNow));
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await WaitReady(pins);
        }
        finally { await loader.StopAsync(TestContext.Current.CancellationToken); }
        Assert.True(pins.Accepts(certificate, DateTimeOffset.UtcNow));
    }

    private static async Task WaitReady(AgentPinRegistry pins)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (pins.NeedsRefresh) await Task.Delay(10, deadline.Token);
    }

    public class DispatchProxyReader : System.Reflection.DispatchProxy
    {
        private Func<CancellationToken, Task<IReadOnlyList<AgentPublicPin>>> read = null!;
        public static IAgentControlReader Create(Func<CancellationToken, Task<IReadOnlyList<AgentPublicPin>>> read)
        {
            var proxy = Create<IAgentControlReader, DispatchProxyReader>();
            ((DispatchProxyReader)(object)proxy).read = read;
            return proxy;
        }
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => method?.Name == nameof(IAgentControlReader.ReadPinsAsync)
            ? read((CancellationToken)args![0]!) : throw new NotSupportedException();
    }
}
