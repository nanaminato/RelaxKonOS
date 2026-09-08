using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Intentionally narrow: a caller can use the controller secret but cannot enumerate or export it.</summary>
public interface IProxyControllerSecretStore
{
    Task<string> GetOrCreateAsync(CancellationToken cancellationToken);
}

/// <summary>Machine-local encrypted secret persistence until the host-global Proxy store is introduced in Goal 4.</summary>
public sealed class DataProtectionProxyControllerSecretStore : IProxyControllerSecretStore
{
    private readonly IDataProtector _protector;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DataProtectionProxyControllerSecretStore(IHostEnvironment environment, IDataProtectionProvider protectionProvider)
    {
        _protector = protectionProvider.CreateProtector("RelaxKonOS.Proxy.ControllerSecret.v1");
        _path = Path.Combine(environment.ContentRootPath, "data", "proxy-controller.secret");
    }

    public async Task<string> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_path))
            {
                try { return _protector.Unprotect(await File.ReadAllTextAsync(_path, cancellationToken)); }
                catch { throw new ProxyControllerSecretException(); }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bytes = RandomNumberGenerator.GetBytes(32);
            var secret = Convert.ToBase64String(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            var staging = _path + ".new";
            await File.WriteAllTextAsync(staging, _protector.Protect(secret), Encoding.UTF8, cancellationToken);
            File.Move(staging, _path, overwrite: false);
            return secret;
        }
        finally { _gate.Release(); }
    }
}

public sealed class ProxyControllerSecretException : Exception
{
    public ProxyControllerSecretException() : base("proxy.controller_secret_unavailable") { }
}
