using System.Text.RegularExpressions;

namespace FlowPlane.Domain.Providers;

public enum SecretReferenceScheme
{
    FlowPlane,
    Environment,
}

public sealed partial class SecretReference : IEquatable<SecretReference>
{
    private readonly string locator;

    private SecretReference(SecretReferenceScheme scheme, string locator)
    {
        Scheme = scheme;
        this.locator = locator;
    }

    public SecretReferenceScheme Scheme { get; }

    public static SecretReference FlowPlane(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Managed secret identifier must not be empty.", nameof(id));
        }

        return new SecretReference(SecretReferenceScheme.FlowPlane, id.ToString("D"));
    }

    public static SecretReference Environment(string variableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);

        if (!EnvironmentVariableName().IsMatch(variableName))
        {
            throw new ArgumentException("Environment secret reference has an invalid variable name.", nameof(variableName));
        }

        return new SecretReference(SecretReferenceScheme.Environment, variableName);
    }

    public static SecretReference Parse(string canonicalValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalValue);

        const string flowPlanePrefix = "flowplane:";
        const string environmentPrefix = "env:";

        if (canonicalValue.StartsWith(flowPlanePrefix, StringComparison.Ordinal))
        {
            var value = canonicalValue[flowPlanePrefix.Length..];
            if (!Guid.TryParseExact(value, "D", out var id) ||
                !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
            {
                throw new ArgumentException("Managed secret reference must contain a lowercase GUID.", nameof(canonicalValue));
            }

            return FlowPlane(id);
        }

        if (canonicalValue.StartsWith(environmentPrefix, StringComparison.Ordinal))
        {
            return Environment(canonicalValue[environmentPrefix.Length..]);
        }

        throw new ArgumentException("Secret reference scheme is unsupported.", nameof(canonicalValue));
    }

    public string ToCanonicalString() => Scheme switch
    {
        SecretReferenceScheme.FlowPlane => $"flowplane:{locator}",
        SecretReferenceScheme.Environment => $"env:{locator}",
        _ => throw new InvalidOperationException("Secret reference scheme is unsupported."),
    };

    public bool TryGetFlowPlaneId(out Guid id)
    {
        if (Scheme == SecretReferenceScheme.FlowPlane)
        {
            return Guid.TryParseExact(locator, "D", out id);
        }

        id = default;
        return false;
    }

    public string GetEnvironmentVariableName()
    {
        if (Scheme != SecretReferenceScheme.Environment)
        {
            throw new InvalidOperationException("Secret reference is not environment-backed.");
        }

        return locator;
    }

    public bool Equals(SecretReference? other) =>
        other is not null && Scheme == other.Scheme &&
        string.Equals(locator, other.locator, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as SecretReference);

    public override int GetHashCode() => HashCode.Combine(Scheme, StringComparer.Ordinal.GetHashCode(locator));

    public static bool operator ==(SecretReference? left, SecretReference? right) =>
        Equals(left, right);

    public static bool operator !=(SecretReference? left, SecretReference? right) =>
        !Equals(left, right);

    public override string ToString() => Scheme switch
    {
        SecretReferenceScheme.FlowPlane => "flowplane:[redacted]",
        SecretReferenceScheme.Environment => "env:[redacted]",
        _ => "[unsupported-secret-reference]",
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariableName();
}
