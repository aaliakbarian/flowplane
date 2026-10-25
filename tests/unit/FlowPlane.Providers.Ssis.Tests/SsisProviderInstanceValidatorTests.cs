using System.Text.Json;

using FlowPlane.Application.Providers;
using FlowPlane.Application.Secrets;
using FlowPlane.Domain.Providers;
using FlowPlane.Providers.Ssis.Configuration;
using FlowPlane.Providers.Ssis.Validation;

namespace FlowPlane.Providers.Ssis.Tests;

public sealed class SsisProviderInstanceValidatorTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Changed_context_after_password_resolution_never_reaches_the_native_probe(bool contextCurrent, int expectedProbes)
    {
        var resolved = false;
        var guardCalled = false;
        var probe = new StubProbe((_, _, _) => Task.FromResult(SuccessfulProbeResult(
            ProviderCapabilityAvailability.Available, ProviderCapabilityAvailability.Available)));
        var validator = CreateValidator(new ObservedResolver(() => resolved = true), probe, false);
        Func<CancellationToken, ValueTask<bool>> current = _ =>
        {
            Assert.True(resolved);
            guardCalled = true;
            return ValueTask.FromResult(contextCurrent);
        };
        var result = await validator.ValidateAsync(CreateProviderInstance(), TestContext.Current.CancellationToken, current);
        Assert.True(guardCalled);
        Assert.Equal(contextCurrent, result.IsValid);
        Assert.Equal(expectedProbes, probe.CallCount);
        if (!contextCurrent) Assert.Contains(result.Checks, check => check.Code == "ssis.configuration.context-changed");
    }

    private sealed class ObservedResolver(Action observed) : ISecretResolver
    {
        public ValueTask<T> UseAsync<T>(SecretReference reference,
            Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<T>> use, CancellationToken cancellationToken = default)
        {
            observed();
            return use("synthetic-new-context-password".AsMemory(), cancellationToken);
        }
    }

    [Fact]
    public async Task Rehydrated_provider_type_retains_the_A10_validation_path()
    {
        var original = CreateProviderInstance();
        var rehydrated = ProviderInstance.Create(original.Id, ProviderType.Parse("ssis"), original.DisplayName,
            true, original.Configuration, original.SecretReference, DateTimeOffset.UtcNow);
        var validator = CreateValidator(new StubSecretResolver("secret"), new StubProbe((_, _, _) =>
            Task.FromResult(SuccessfulProbeResult(ProviderCapabilityAvailability.Unknown, ProviderCapabilityAvailability.Unknown))), false);
        var result = await validator.ValidateAsync(rehydrated, TestContext.Current.CancellationToken);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Successful_contact_remains_valid_when_capabilities_are_unknown()
    {
        var probeResult = SuccessfulProbeResult(
            ProviderCapabilityAvailability.Unknown,
            ProviderCapabilityAvailability.Unknown);
        var probe = new StubProbe((_, _, _) => Task.FromResult(probeResult));
        var validator = CreateValidator(
            new StubSecretResolver("secret"),
            probe,
            isDevelopment: false);

        var result = await validator.ValidateAsync(
            CreateProviderInstance(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(ProviderValidationOutcome.Succeeded, result.Outcome);
        Assert.All(result.Capabilities, capability =>
            Assert.Equal(ProviderCapabilityAvailability.Unknown, capability.Availability));
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task Certificate_bypass_is_rejected_outside_development_before_secret_or_network_use()
    {
        var resolver = new StubSecretResolver("secret");
        var probe = new StubProbe((_, _, _) => Task.FromResult(SuccessfulProbeResult(
            ProviderCapabilityAvailability.Available,
            ProviderCapabilityAvailability.Available)));
        var validator = CreateValidator(resolver, probe, isDevelopment: false);

        var result = await validator.ValidateAsync(
            CreateProviderInstance(trustServerCertificate: true),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Checks, check =>
            check.Code == "ssis.configuration.tls-policy-rejected" &&
            check.Status == ProviderValidationCheckStatus.Failed);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task Certificate_bypass_is_allowed_when_explicit_in_development()
    {
        var probe = new StubProbe((configuration, _, _) =>
        {
            Assert.True(configuration.TrustServerCertificate);
            return Task.FromResult(SuccessfulProbeResult(
                ProviderCapabilityAvailability.Unknown,
                ProviderCapabilityAvailability.Unknown));
        });
        var validator = CreateValidator(
            new StubSecretResolver("secret"),
            probe,
            isDevelopment: true);

        var result = await validator.ValidateAsync(
            CreateProviderInstance(trustServerCertificate: true),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Secret_resolution_failure_is_safe_and_blocks_later_checks()
    {
        var canary = "FLOWPLANE_A10_SECRET_LOCATOR_CANARY";
        var resolver = new StubSecretResolver(new SecretResolutionException(
            SecretErrorCode.SecretNotFound,
            "The referenced secret is unavailable."));
        var probe = new StubProbe((_, _, _) => throw new InvalidOperationException("must not run"));
        var validator = CreateValidator(resolver, probe, isDevelopment: false);

        var result = await validator.ValidateAsync(
            CreateProviderInstance(secretReference: SecretReference.Environment(canary)),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Checks, check =>
            check.Code == "ssis.secret.unavailable" &&
            check.Status == ProviderValidationCheckStatus.Failed);
        Assert.Contains(result.Checks, check =>
            check.Code == "ssis.endpoint.reachable" &&
            check.Status == ProviderValidationCheckStatus.Unavailable);
        Assert.DoesNotContain(canary, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task Resolved_password_is_absent_from_results_and_used_once()
    {
        const string canary = "a10-resolved-password-canary";
        var probe = new StubProbe((_, password, _) =>
        {
            Assert.Equal(canary, new string(password.Span));
            return Task.FromResult(SuccessfulProbeResult(
                ProviderCapabilityAvailability.Available,
                ProviderCapabilityAvailability.Unavailable));
        });
        var validator = CreateValidator(new StubSecretResolver(canary), probe, isDevelopment: false);

        var result = await validator.ValidateAsync(
            CreateProviderInstance(),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(canary, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        var probe = new StubProbe(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return SuccessfulProbeResult(
                ProviderCapabilityAvailability.Unknown,
                ProviderCapabilityAvailability.Unknown);
        });
        var validator = CreateValidator(new StubSecretResolver("secret"), probe, false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            validator.ValidateAsync(CreateProviderInstance(), cancellation.Token));
    }

    [Fact]
    public async Task Overall_timeout_is_sanitized_and_does_not_retry()
    {
        var probe = new StubProbe(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return SuccessfulProbeResult(
                ProviderCapabilityAvailability.Unknown,
                ProviderCapabilityAvailability.Unknown);
        });
        var validator = new SsisProviderInstanceValidator(
            new SsisProviderConfigurationCodec(),
            new StubSecretResolver("secret"),
            probe,
            new SsisValidationSecurityPolicy(IsDevelopment: false),
            new SsisValidationOptions(5, 10, 1));

        var result = await validator.ValidateAsync(
            CreateProviderInstance(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProviderValidationOutcome.Unavailable, result.Outcome);
        Assert.Contains(result.Checks, check => check.Code == "ssis.validation.timeout");
        Assert.Equal(1, probe.CallCount);
    }

    private static SsisProviderInstanceValidator CreateValidator(
        ISecretResolver resolver,
        ISsisValidationProbe probe,
        bool isDevelopment) => new(
            new SsisProviderConfigurationCodec(),
            resolver,
            probe,
            new SsisValidationSecurityPolicy(isDevelopment),
            SsisValidationOptions.Default);

    private static ProviderInstance CreateProviderInstance(
        bool trustServerCertificate = false,
        SecretReference? secretReference = null)
    {
        var codec = new SsisProviderConfigurationCodec();
        var configuration = codec.Encode(new SsisProviderConfiguration(
            "sql.example.internal",
            1433,
            "flowplane_runtime",
            trustServerCertificate));
        return ProviderInstance.Create(
            Guid.Parse("277ffb25-284d-4c5b-bec1-c297fae9c86f"),
            SsisProviderConfigurationCodec.SsisProviderType,
            "SSIS test",
            isEnabled: true,
            configuration,
            secretReference ?? SecretReference.Environment("FLOWPLANE_A10_TEST_PASSWORD"),
            DateTimeOffset.Parse("2026-10-13T00:00:00Z"));
    }

    private static ProviderValidationResult SuccessfulProbeResult(
        ProviderCapabilityAvailability discovery,
        ProviderCapabilityAvailability execution) => new(
            ProviderValidationOutcome.Succeeded,
            [new ProviderValidationCheck(
                "ssis.catalog.surface-compatible",
                ProviderValidationCheckStatus.Succeeded,
                "The required SSISDB catalog surface is compatible.")],
            [
                new ProviderCapabilityResult("discovery", discovery, "test.discovery"),
                new ProviderCapabilityResult("execution", execution, "test.execution"),
            ],
            [new ProviderValidationFact("ssis.sql-server.major-version", "16")]);

    private sealed class StubProbe(
        Func<SsisProviderConfiguration, ReadOnlyMemory<char>, CancellationToken,
            Task<ProviderValidationResult>> handler) : ISsisValidationProbe
    {
        public int CallCount { get; private set; }

        public Task<ProviderValidationResult> ValidateAsync(
            SsisProviderConfiguration configuration,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return handler(configuration, password, cancellationToken);
        }
    }

    private sealed class StubSecretResolver : ISecretResolver
    {
        private readonly string? value;
        private readonly SecretResolutionException? exception;

        public StubSecretResolver(string value)
        {
            this.value = value;
        }

        public StubSecretResolver(SecretResolutionException exception)
        {
            this.exception = exception;
        }

        public int CallCount { get; private set; }

        public async ValueTask<TResult> UseAsync<TResult>(
            SecretReference reference,
            Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<TResult>> use,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (exception is not null)
            {
                throw exception;
            }

            return await use(value!.AsMemory(), cancellationToken);
        }
    }
}
