using RelaxKonOS.Client.Apps;
using RelaxKonOS.Client.Apps.CodeEditor;
using RelaxKonOS.Client.Apps.ImageViewer;
using RelaxKonOS.Client.Apps.Notepad;
using RelaxKonOS.Client.Apps.Settings;
using RelaxKonOS.Client.Apps.Terminal;
using RelaxKonOS.Client.Apps.Welcome;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.Client.Services;

/// <summary>The one compiled-in source of BuiltIn keys, AppIds, and application factories.</summary>
public sealed class BuiltInApplicationRegistry : IBuiltInApplicationFactoryRegistry
{
    private readonly BuiltInApplicationFactoryRegistry _inner;

    public BuiltInApplicationRegistry(IServiceProvider services)
    {
        _inner = new BuiltInApplicationFactoryRegistry(new[]
        {
            Define<WelcomeApp>("welcome", "relaxkonos.welcome", services),
            Define<NotepadApp>("notepad", "relaxkonos.notepad", services),
            Define<CodeEditorApp>("codeeditor", "relaxkonos.codeeditor", services),
            Define<ImageViewerApp>("imageviewer", "relaxkonos.imageviewer", services),
            Define<SettingsApp>("settings", "relaxkonos.settings", services),
            Define<TerminalApp>("terminal", "relaxkonos.terminal", services),
            Define<RelaxKonOS.Client.Apps.Explorer.ExplorerApp>("explorer", "relaxkonos.explorer", services),
            Define<RelaxKonOS.Client.Apps.Browser.BrowserApp>("browser", "relaxkonos.browser", services),
            Define<RelaxKonOS.Client.Apps.PortForwarding.PortForwardingApp>("port-forwarding", "relaxkonos.port-forwarding", services),
            Define<RelaxKonOS.Client.Apps.TaskManager.TaskManagerApp>("taskmanager", "relaxkonos.taskmanager", services),
            Define<RelaxKonOS.Client.Apps.Docker.DockerManagerApp>("docker", "relaxkonos.docker", services),
            Define<RelaxKonOS.Client.Apps.ProcessGuardian.ProcessGuardianApp>("processguardian", "relaxkonos.processguardian", services),
            Define<RelaxKonOS.Client.Apps.Firewall.FirewallApp>("firewall", "relaxkonos.firewall", services),
            Define<RelaxKonOS.Client.Apps.Certificates.CertificateManagerApp>("certificates", "relaxkonos.certificates", services),
            Define<RelaxKonOS.Client.Apps.WebServers.WebServerManagerApp>("webservers", "relaxkonos.webservers", services),
            Define<RelaxKonOS.Client.Apps.FileServices.FileServicesApp>("file-services", "relaxkonos.file-services", services),
            Define<RelaxKonOS.Client.Apps.Tunnels.TunnelManagerApp>("tunnels", "relaxkonos.tunnels", services),
            Define<RelaxKonOS.Client.Apps.Proxy.ProxyManagerApp>("proxy", "relaxkonos.proxy", services),
            Define<RelaxKonOS.Client.Apps.Git.GitClientApp>("git", "relaxkonos.git", services),
            Define<RelaxKonOS.Client.Apps.AppInstaller.AppInstallerApp>("appinstaller", "relaxkonos.appinstaller", services),
            Define<RelaxKonOS.Client.Apps.Registry.RegistryApp>("registry", "relaxkonos.registry", services),
        });
    }

    public IReadOnlyCollection<BuiltInApplicationDefinition> Definitions => _inner.Definitions;
    public bool TryGet(string builtinKey, out BuiltInApplicationDefinition definition) => _inner.TryGet(builtinKey, out definition);

    private static BuiltInApplicationDefinition Define<T>(string key, string appId, IServiceProvider services)
        where T : class, IRemoteApplication
    {
        var application = services.GetRequiredService<T>();
        var id = new AppId(appId);
        if (application.Manifest.Id != id)
            throw new InvalidOperationException($"Built-in factory '{key}' has an unexpected AppId.");
        return new BuiltInApplicationDefinition(key, id, application.Manifest,
            provider => provider.GetRequiredService<T>());
    }
}
