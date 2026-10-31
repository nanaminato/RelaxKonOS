using Microsoft.Extensions.Logging;

namespace RelaxKonOS.Server.Residency;

/// <summary>
/// A disposable service graph for one module. The root container stays alive for the whole process and
/// must not hold releasable module implementations, so a module that wants to be releasable registers
/// its services here instead. Root services (configuration, storage factories, logging, file access)
/// are borrowed, never disposed here: only objects this container created are released.
/// </summary>
internal sealed class ModuleServiceContainer : IServiceProvider, IAsyncDisposable
{
    private readonly string _moduleId;
    private readonly IServiceProvider _root;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Type, Func<ModuleServiceContainer, object>> _factories = new();
    private readonly Dictionary<Type, object> _singletons = new();
    private readonly HashSet<Type> _resolving = new();
    private readonly HashSet<object> _tracked = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _created = new();
    private bool _disposed;
    private Task? _disposal;

    internal ModuleServiceContainer(string moduleId, IServiceProvider root)
    {
        _moduleId = moduleId;
        _root = root;
        _logger = (root.GetService(typeof(ILoggerFactory)) as ILoggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
            .CreateLogger($"RelaxKonOS.Server.Residency.{moduleId}");
    }

    /// <summary>Registers a module-owned singleton. The factory runs at most once per runtime.</summary>
    internal void AddSingleton<TService>(Func<ModuleServiceContainer, TService> factory) where TService : class =>
        _factories[typeof(TService)] = container => factory(container);

    /// <summary>
    /// Activates a concrete module-owned type and tracks it for disposal. Constructor dependencies are
    /// resolved from this container first and from the root container second.
    /// </summary>
    internal TService Create<TService>() where TService : class => (TService)Create(typeof(TService));

    internal object Create(Type concreteType)
    {
        var instance = ActivatorUtilities.CreateInstance(this, concreteType);
        Track(instance);
        return instance;
    }

    internal TService GetRequired<TService>() where TService : class => (TService)GetRequired(typeof(TService));

    internal object GetRequired(Type serviceType) =>
        GetService(serviceType) ?? throw new InvalidOperationException(
            $"Server module {_moduleId} cannot resolve {serviceType.FullName}: neither the module runtime nor the Host registers it.");

    /// <summary>Returns the already-created instance without creating it; used by unload checks.</summary>
    internal bool TryGetSingleton(Type serviceType, out object? instance)
    {
        lock (_gate)
        {
            return _singletons.TryGetValue(serviceType, out instance);
        }
    }

    public object? GetService(Type serviceType)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException($"module runtime {_moduleId}");
            if (_singletons.TryGetValue(serviceType, out var existing)) return existing;
            if (_factories.ContainsKey(serviceType)) return CreateSingletonLocked(serviceType);
        }

        // Borrowed from the Host: not owned, so it is never disposed with this container.
        return _root.GetService(serviceType);
    }

    private object CreateSingletonLocked(Type serviceType)
    {
        if (!_resolving.Add(serviceType))
            throw new InvalidOperationException(
                $"Circular server module service dependency while creating {serviceType.FullName} in {_moduleId}.");
        try
        {
            var instance = _factories[serviceType](this);
            _singletons[serviceType] = instance;
            Track(instance);
            return instance;
        }
        finally
        {
            _resolving.Remove(serviceType);
        }
    }

    private void Track(object instance)
    {
        if (instance is not (IDisposable or IAsyncDisposable)) return;
        lock (_gate)
        {
            if (_tracked.Add(instance)) _created.Add(instance);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_disposal ??= DisposeOwnedAsync());
    }

    private async Task DisposeOwnedAsync()
    {
        object[] created;
        lock (_gate)
        {
            _disposed = true;
            created = _created.ToArray();
            _created.Clear();
            _tracked.Clear();
            _singletons.Clear();
        }

        List<Exception> failures = [];
        List<object> retained = [];
        for (var index = created.Length - 1; index >= 0; index--)
        {
            try
            {
                switch (created[index])
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception exception)
            {
                // Continue tearing down independent resources, but preserve the failure and original
                // object so the coordinator quarantines this generation instead of starting another.
                failures.Add(exception);
                retained.Add(created[index]);
                _logger.LogError(exception, "Releasing {Type} for server module {ModuleId} failed",
                    created[index].GetType().FullName, _moduleId);
            }
        }
        if (failures.Count > 0)
        {
            lock (_gate) _created.AddRange(retained);
            throw new AggregateException("Module resource release requires recovery.", failures);
        }
    }
}
