using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Residency;

/// <summary>
/// The object graph the Host may release when nobody uses a module. A runtime owns every disposable
/// instance it creates; disposing it must free services, caches, timers and connections without
/// touching module files, configuration or business data. The assembly may stay loaded: residency is
/// an object-lifetime feature, and releasing it must never be described as an assembly unload.
/// </summary>
internal interface IServerModuleRuntime : IAsyncDisposable
{
    string ModuleId { get; }

    /// <summary>Resolves a module-owned service. Called only while a <see cref="ModuleLease"/> is held.</summary>
    object Resolve(Type serviceType);

    /// <summary>
    /// Asked immediately before disposal. A non-null answer keeps the runtime alive and is reported as
    /// the reason the module cannot be released yet; a runtime must not mutate durable state here.
    /// </summary>
    ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken cancellationToken);
}

/// <summary>Creates the module runtime on demand. One factory per enabled, releasable module.</summary>
internal interface IServerModuleRuntimeFactory
{
    string ModuleId { get; }

    ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context,
        CancellationToken cancellationToken);
}
