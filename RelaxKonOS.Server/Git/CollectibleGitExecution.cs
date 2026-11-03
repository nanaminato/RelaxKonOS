using System.Reflection;
using System.Runtime.Loader;

namespace RelaxKonOS.Server.Git;

/// <summary>Host wrapper holds code only for the active generation; no implementation Type is cached.</summary>
internal sealed class CollectibleGitExecution : IAsyncDisposable
{
    private AssemblyLoadContext? _context;
    private IGitExecutionModule? _module;
    internal WeakReference CodeReference { get; }
    internal IGitExecutionModule Module => _module ?? throw new ObjectDisposedException(nameof(CollectibleGitExecution));

    internal CollectibleGitExecution(GitExecutionContext services)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Modules", "Git");
        var entry = Path.Combine(directory, "RelaxKonOS.ServerModule.Git.dll");
        if (!File.Exists(entry) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bundled Git execution assembly is missing or linked.");
        var context = new ExecutionLoadContext();
        CodeReference = new WeakReference(context);
        try
        {
            var assembly = context.LoadFromAssemblyPath(entry);
            var type = assembly.GetType("RelaxKonOS.Server.Git.GitExecutionModule", throwOnError: true)!;
            _module = (IGitExecutionModule)Activator.CreateInstance(type, services)!;
            _context = context;
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // On failure retain the generation for the coordinator's recovery quarantine.
        if (_module is null) return;
        await _module.DisposeAsync().ConfigureAwait(false);
        _module = null;
        var context = _context;
        _context = null;
        context?.Unload();
    }

    private sealed class ExecutionLoadContext() : AssemblyLoadContext("git-execution:" + Guid.NewGuid().ToString("N"), isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            // This execution module currently has no private managed/native dependencies.
            // Shared contracts and framework dependencies must retain Host type identity.
            if (name.Name == "RelaxKonOS.Server")
                throw new InvalidDataException("Git execution cannot depend on Server implementation.");
            return null;
        }
    }
}
