using System.Reflection;

namespace RelaxKonOS.Server.Residency;

/// <summary>
/// Forwards every call to a module-owned service through a residency lease. Static routes and other
/// domains therefore never hold a releasable instance: they hold this gate, which loads the module on
/// first use, keeps it alive for the duration of the call and lets the idle sweep release it afterwards.
///
/// <para>
/// The gate deliberately knows nothing about the service's methods, so adding a repository operation
/// can not accidentally bypass residency. A failing load surfaces as
/// <see cref="ModuleUnavailableException"/> before any module code runs.
/// </para>
/// </summary>
public class ModuleServiceProxy<TService> : DispatchProxy where TService : class
{
    private static readonly MethodInfo TaskOfTypeMethod = typeof(ModuleServiceProxy<TService>)
        .GetMethod(nameof(InvokeTaskOfType), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ValueTaskOfTypeMethod = typeof(ModuleServiceProxy<TService>)
        .GetMethod(nameof(InvokeValueTaskOfType), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private ModuleResidencyCoordinator _coordinator = null!;
    private string _moduleId = null!;

    internal static TService Create(ModuleResidencyCoordinator coordinator, string moduleId)
    {
        var proxy = DispatchProxy.Create<TService, ModuleServiceProxy<TService>>();
        var self = (ModuleServiceProxy<TService>)(object)proxy;
        self._coordinator = coordinator;
        self._moduleId = moduleId;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null) throw new ArgumentNullException(nameof(targetMethod));

        var cancellationToken = ExtractCancellationToken(targetMethod, args);
        var leaseTask = _coordinator.AcquireAsync(_moduleId, cancellationToken);
        var returnType = targetMethod.ReturnType;

        if (returnType == typeof(void))
        {
            using var lease = leaseTask.GetAwaiter().GetResult();
            InvokeTarget(targetMethod, args, lease);
            return null;
        }

        if (returnType == typeof(Task)) return InvokeTaskAsync(targetMethod, args, leaseTask);
        if (returnType == typeof(ValueTask)) return InvokeValueTaskAsync(targetMethod, args, leaseTask);

        if (returnType.IsGenericType)
        {
            var definition = returnType.GetGenericTypeDefinition();
            var resultType = returnType.GetGenericArguments()[0];
            if (definition == typeof(Task<>))
                return TaskOfTypeMethod.MakeGenericMethod(resultType).Invoke(this, [targetMethod, args, leaseTask]);
            if (definition == typeof(ValueTask<>))
                return ValueTaskOfTypeMethod.MakeGenericMethod(resultType).Invoke(this, [targetMethod, args, leaseTask]);
        }

        var acquired = leaseTask.GetAwaiter().GetResult();
        using (acquired)
        {
            return InvokeTarget(targetMethod, args, acquired);
        }
    }

    private async Task InvokeTaskAsync(MethodInfo method, object?[]? args, Task<ModuleLease> leaseTask)
    {
        using var lease = await leaseTask.ConfigureAwait(false);
        await ((Task)InvokeResult(method, args, lease)).ConfigureAwait(false);
    }

    private async Task<TResult> InvokeTaskOfType<TResult>(MethodInfo method, object?[]? args, Task<ModuleLease> leaseTask)
    {
        using var lease = await leaseTask.ConfigureAwait(false);
        return await ((Task<TResult>)InvokeResult(method, args, lease)).ConfigureAwait(false);
    }

    private async ValueTask InvokeValueTaskAsync(MethodInfo method, object?[]? args, Task<ModuleLease> leaseTask)
    {
        using var lease = await leaseTask.ConfigureAwait(false);
        await ((ValueTask)InvokeResult(method, args, lease)).ConfigureAwait(false);
    }

    private async ValueTask<TResult> InvokeValueTaskOfType<TResult>(MethodInfo method, object?[]? args,
        Task<ModuleLease> leaseTask)
    {
        using var lease = await leaseTask.ConfigureAwait(false);
        return await ((ValueTask<TResult>)InvokeResult(method, args, lease)).ConfigureAwait(false);
    }

    private static object InvokeResult(MethodInfo method, object?[]? args, ModuleLease lease) =>
        InvokeTarget(method, args, lease)
        ?? throw new InvalidOperationException($"{typeof(TService).FullName}.{method.Name} returned no result object.");

    private static object? InvokeTarget(MethodInfo method, object?[]? args, ModuleLease lease) =>
        method.Invoke(lease.Runtime.Resolve(typeof(TService)), args);

    private static CancellationToken ExtractCancellationToken(MethodInfo method, object?[]? args)
    {
        if (args is null) return default;
        var parameters = method.GetParameters();
        for (var index = 0; index < parameters.Length && index < args.Length; index++)
            if (parameters[index].ParameterType == typeof(CancellationToken) && args[index] is CancellationToken token)
                return token;
        return default;
    }
}
