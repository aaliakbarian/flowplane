namespace FlowPlane.Providers.Ssis.Configuration;

public sealed class ProviderConfigurationException : Exception
{
    public ProviderConfigurationException(string safeMessage)
        : base(safeMessage)
    {
    }

    public ProviderConfigurationException(string safeMessage, Exception innerException)
        : base(safeMessage, innerException)
    {
    }
}
