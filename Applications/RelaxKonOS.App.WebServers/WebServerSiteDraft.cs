using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.App.WebServers;

/// <summary>Editable, UI-local representation of one domain and HTTP-port pair.</summary>
public sealed partial class WebServerSiteBindingEditor : ObservableObject
{
    public WebServerSiteBindingEditor(string domain = "", int port = 80)
    {
        Domain = domain;
        Port = port;
    }

    [ObservableProperty] private string _domain = string.Empty;
    [ObservableProperty] private int _port = 80;
}

/// <summary>Editable, UI-local representation of a path prefix forwarded to one upstream.</summary>
public sealed partial class WebServerProxyRouteEditor : ObservableObject
{
    public WebServerProxyRouteEditor(string path = "/", string upstream = "", bool disableBuffering = false)
    {
        Path = path;
        Upstream = upstream;
        DisableBuffering = disableBuffering;
    }

    [ObservableProperty] private string _path = "/";
    [ObservableProperty] private string _upstream = string.Empty;
    [ObservableProperty] private bool _disableBuffering;
}

/// <summary>Explicitly selects one HTTPS material source, so managed certificates and raw host
/// files cannot be accidentally submitted together.</summary>
public enum SiteCertificateSource { Managed, ServerFiles }
public sealed record SiteCertificateSourceOption(SiteCertificateSource Value, string Label);
