using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using FlowPlane.Application.Providers;
using FlowPlane.Application.Providers.DiscoveryInventory;
using FlowPlane.Application.Providers.DiscoveryHosting;
using FlowPlane.Application.Secrets;
using FlowPlane.Infrastructure.Persistence;
using FlowPlane.Infrastructure.Persistence.Discovery;
using FlowPlane.Infrastructure.Secrets;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlowPlane.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFlowPlanePostgreSql(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbContext<FlowPlaneDbContext>(options =>
        {
            options.UseNpgsql(PostgreSqlConnectionString.Resolve(configuration));
        });

        AddSecretServices(services, configuration);
        // The API composition root binds operator configuration and replaces this default.
        services.AddSingleton(new DiscoveryInventoryOptions());
        services.AddScoped<IProviderDiscoveryInventoryStore, ProviderDiscoveryInventoryStore>();
        services.AddScoped<IProviderInventoryPageStore, ProviderDiscoveryInventoryStore>();
        services.AddScoped<ProviderDiscoveryRefreshService>();
        services.AddSingleton(new DiscoveryHostingOptions());
        services.AddScoped<IHostedDiscoveryStore, HostedDiscoveryStore>();

        services
            .AddHealthChecks()
            .AddDbContextCheck<FlowPlaneDbContext>(
                "postgresql",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"]);

        return services;
    }

    private static void AddSecretServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var certificate = TryLoadRootCertificate(
            configuration["FlowPlane:Secrets:RootCertificatePath"]);
        if (certificate is not null)
        {
            services.AddSingleton(certificate);
            services
                .AddDataProtection()
                .SetApplicationName("FlowPlane")
                .PersistKeysToDbContext<FlowPlaneDbContext>()
                .ProtectKeysWithCertificate(certificate);
            services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        }
        else
        {
            services.AddSingleton<ISecretProtector, UnavailableSecretProtector>();
        }

        services.AddScoped<EnvironmentSecretResolver>();
        services.AddScoped<ManagedSecretStore>();
        services.AddScoped<IManagedSecretStore>(provider =>
            provider.GetRequiredService<ManagedSecretStore>());
        services.AddScoped<ISecretResolver, SecretResolver>();
        services.AddScoped<IProviderInstanceStore, ProviderInstanceStore>();
    }

    private static X509Certificate2? TryLoadRootCertificate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                path,
                password: null,
                X509KeyStorageFlags.EphemeralKeySet);
            if (certificate.HasPrivateKey)
            {
                return certificate;
            }

            certificate.Dispose();
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
