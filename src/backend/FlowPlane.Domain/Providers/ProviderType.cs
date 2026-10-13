using System.Text.RegularExpressions;

namespace FlowPlane.Domain.Providers;

public sealed partial class ProviderType : IEquatable<ProviderType>
{
    private ProviderType(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ProviderType Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!CanonicalIdentifier().IsMatch(value))
        {
            throw new ArgumentException("Provider type must be a canonical lowercase identifier.", nameof(value));
        }

        return new ProviderType(value);
    }

    public bool Equals(ProviderType? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ProviderType);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalIdentifier();
}
