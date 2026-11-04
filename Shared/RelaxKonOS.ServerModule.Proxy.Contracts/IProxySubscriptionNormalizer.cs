namespace RelaxKonOS.Server.Proxy;

/// <summary>Pure subscription text conversion. No Host services, persistence, network or native authority.</summary>
public interface IProxySubscriptionNormalizer
{
    string Normalize(string content);
}
