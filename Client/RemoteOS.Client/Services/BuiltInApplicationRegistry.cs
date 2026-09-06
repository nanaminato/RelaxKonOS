using Client.Apps;
using Client.Apps.CodeEditor;
using Client.Apps.ImageViewer;
using Client.Apps.Notepad;
using Client.Apps.Settings;
using Client.Apps.Terminal;
using Client.Apps.Welcome;
using Microsoft.Extensions.DependencyInjection;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;

namespace Client.Services;

/// <summary>The one compiled-in source of BuiltIn keys, AppIds, and application factories.</summary>
public sealed class BuiltInApplicationRegistry : IBuiltInApplicationFactoryRegistry
{
    private readonly BuiltInApplicationFactoryRegistry _inner;

    public BuiltInApplicationRegistry(IServiceProvider services)
    {
        _inner = new BuiltInApplicationFactoryRegistry(new[]
        {
            Define<WelcomeApp>("welcome", "remoteos.welcome", services),
            Define<NotepadApp>("notepad", "remoteos.notepad", services),
            Define<CodeEditorApp>("codeeditor", "remoteos.codeeditor", services),
            Define<ImageViewerApp>("imageviewer", "remoteos.imageviewer", services),
            Define<SettingsApp>("settings", "remoteos.settings", services),
            Define<TerminalApp>("terminal", "remoteos.terminal", services),
            Define<Client.Apps.Explorer.ExplorerApp>("explorer", "remoteos.explorer", services),
            Define<Client.Apps.Browser.BrowserApp>("browser", "remoteos.browser", services),
            Define<Client.Apps.PortForwarding.PortForwardingApp>("port-forwarding", "remoteos.port-forwarding", services),
            Define<Client.Apps.TaskManager.TaskManagerApp>("taskmanager", "remoteos.taskmanager", services),
            Define<Client.Apps.Docker.DockerManagerApp>("docker", "remoteos.docker", services),
            Define<Client.Apps.ProcessGuardian.ProcessGuardianApp>("processguardian", "remoteos.processguardian", services),
            Define<Client.Apps.Firewall.FirewallApp>("firewall", "remoteos.firewall", services),
            Define<Client.Apps.Certificates.CertificateManagerApp>("certificates", "remoteos.certificates", services),
            Define<Client.Apps.WebServers.WebServerManagerApp>("webservers", "remoteos.webservers", services),
            Define<Client.Apps.Tunnels.TunnelManagerApp>("tunnels", "remoteos.tunnels", services),
            Define<Client.Apps.Proxy.ProxyManagerApp>("proxy", "remoteos.proxy", services),
            Define<Client.Apps.Git.GitClientApp>("git", "remoteos.git", services),
            Define<Client.Apps.AppInstaller.AppInstallerApp>("appinstaller", "remoteos.appinstaller", services),
            Define<Client.Apps.Registry.RegistryApp>("registry", "remoteos.registry", services),
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
