using System.Collections.Concurrent;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services;

/// <summary>默认程序注册表（单例）。持有当前 Workspace 的 scheme/ext → appId 映射，
/// 供启动路由查询（如点选 http 链接用映射应用打开）。
/// 由 <see cref="PreferencesSync"/> 在登录时加载、<c>WorkspacePreferencesEditor</c> 在编辑保存时同步更新。
/// 映射按 scheme 不区分大小写索引；扩展名以 '.' 开头，scheme 不带（http/mailto）。</summary>
public sealed class DefaultAppRegistry : IUriSchemeDefaultResolver
{
    private readonly ConcurrentDictionary<string, string> _byScheme = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    /// <summary>用服务端 DTO 覆盖当前映射。</summary>
    public void SetMappings(IEnumerable<DefaultAppMappingDto>? mappings)
    {
        _byScheme.Clear();
        foreach (var m in mappings ?? [])
            if (!string.IsNullOrWhiteSpace(m.Scheme) && !string.IsNullOrWhiteSpace(m.AppId))
                _byScheme[m.Scheme.Trim()] = m.AppId.Trim();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>查询某个 scheme 或扩展名对应的应用 Id；未配置返回 null。</summary>
    public string? Resolve(string schemeOrExt)
    {
        if (string.IsNullOrWhiteSpace(schemeOrExt)) return null;
        return _byScheme.TryGetValue(schemeOrExt.Trim(), out var appId) ? appId : null;
    }

    AppId? IUriSchemeDefaultResolver.ResolveDefaultApplication(string scheme)
    {
        var appId = Resolve(scheme);
        return string.IsNullOrWhiteSpace(appId) ? null : new AppId(appId);
    }

    /// <summary>当前映射快照（只读）。</summary>
    public IReadOnlyList<DefaultAppMappingDto> Snapshot
        => _byScheme.Select(kv => new DefaultAppMappingDto(kv.Key, kv.Value)).ToList();
}
