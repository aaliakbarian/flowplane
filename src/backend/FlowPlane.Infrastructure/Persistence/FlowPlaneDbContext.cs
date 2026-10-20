using FlowPlane.Domain.Providers;
using FlowPlane.Infrastructure.Persistence.Discovery;

using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FlowPlane.Infrastructure.Persistence;

public sealed class FlowPlaneDbContext(DbContextOptions<FlowPlaneDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<ProviderInstance> ProviderInstances => Set<ProviderInstance>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    internal DbSet<SecretRecord> SecretRecords => Set<SecretRecord>();
    internal DbSet<DiscoveryState> DiscoveryStates => Set<DiscoveryState>();
    internal DbSet<DiscoveryGeneration> DiscoveryGenerations => Set<DiscoveryGeneration>();
    internal DbSet<DiscoveryObservationRecord> DiscoveryObservations => Set<DiscoveryObservationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        DiscoveryModelConfiguration.Configure(modelBuilder);

        modelBuilder.Entity<DataProtectionKey>(builder =>
        {
            builder.ToTable("DataProtectionKeys");
        });

        var providerTypeConverter = new ValueConverter<ProviderType, string>(
            value => value.Value,
            value => ProviderType.Parse(value));
        var providerTypeComparer = new ValueComparer<ProviderType>(
            (left, right) => left != null && left.Equals(right),
            value => value.GetHashCode(),
            value => ProviderType.Parse(value.Value));
        var secretReferenceConverter = new ValueConverter<SecretReference, string>(
            value => value.ToCanonicalString(),
            value => SecretReference.Parse(value));
        var secretReferenceComparer = new ValueComparer<SecretReference>(
            (left, right) => left != null && left.Equals(right),
            value => value.GetHashCode(),
            value => SecretReference.Parse(value.ToCanonicalString()));
        var configurationConverter = new ValueConverter<ProviderConfigurationDocument, string>(
            value => ProviderConfigurationPersistence.Serialize(value),
            value => ProviderConfigurationPersistence.Deserialize(value));
        var configurationComparer = new ValueComparer<ProviderConfigurationDocument>(
            (left, right) => left != null && left.Equals(right),
            value => value.GetHashCode(),
            value => ProviderConfigurationPersistence.Deserialize(
                ProviderConfigurationPersistence.Serialize(value)));

        modelBuilder.Entity<ProviderInstance>(builder =>
        {
            builder.ToTable("ProviderInstances");
            builder.HasKey(instance => instance.Id);
            builder.Property(instance => instance.ProviderType)
                .HasConversion(providerTypeConverter)
                .Metadata.SetValueComparer(providerTypeComparer);
            builder.Property(instance => instance.ProviderType).HasMaxLength(64);
            builder.Property(instance => instance.DisplayName).HasMaxLength(200);
            builder.Property(instance => instance.Configuration)
                .HasConversion(configurationConverter)
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(configurationComparer);
            builder.Property(instance => instance.SecretReference)
                .HasConversion(secretReferenceConverter)
                .HasMaxLength(256)
                .Metadata.SetValueComparer(secretReferenceComparer);
            builder.Property(instance => instance.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<SecretRecord>(builder =>
        {
            builder.ToTable("FlowPlaneSecrets");
            builder.HasKey(secret => secret.Id);
            builder.Property(secret => secret.Ciphertext).IsRequired();
            builder.Property(secret => secret.ProtectionScheme).HasMaxLength(64);
            builder.Property(secret => secret.FormatVersion).HasMaxLength(32);
            builder.Property(secret => secret.Version).IsConcurrencyToken();
        });
    }
}
