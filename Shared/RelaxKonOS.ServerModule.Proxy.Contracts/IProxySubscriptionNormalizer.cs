namespace RelaxKonOS.Server.Proxy;

/// <summary>Decodes and converts a body already opened and authorized by the Host.</summary>
public interface IProxySubscriptionNormalizer
{
    Task<ProxySubscriptionBody> ReadAsync(Stream stream, CancellationToken cancellationToken);
}

public readonly record struct ProxySubscriptionBody(string? Content, int Bytes, bool TooLarge = false)
{
    public const int MaximumBytes = 1_048_576;
}
