using RelaxKonOS.Client.Apps;
using RelaxKonOS.Client.Apps.Settings;
using RelaxKonOS.Client.Apps.Terminal;
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
            Define<RelaxKonOS.Client.Apps.ComponentCenter.ComponentCenterApp>("component-center", "relaxkonos.component-center", services),
            Define<SettingsApp>("settings", "relaxkonos.settings", services),
            Define<RelaxKonOS.Client.Apps.ServerCenter.ServerCenterApp>("server-center", "relaxkonos.server-center", services),
            Define<RelaxKonOS.Client.Apps.ServerCenter.SshFileBrowserApp>("ssh-files", "relaxkonos.ssh-files", services),
            Define<RelaxKonOS.Client.Apps.Explorer.ExplorerApp>("explorer", "relaxkonos.explorer", services),
            Define<RelaxKonOS.Client.Apps.Databases.DatabaseApp>("database", "relaxkonos.database", services),
            Define<RelaxKonOS.Client.Apps.AppInstaller.AppInstallerApp>("appinstaller", "relaxkonos.appinstaller", services),
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
