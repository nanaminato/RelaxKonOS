using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace RelaxKonOS.Server.Residency;

/// <summary>
/// Forwards every call to a module-owned service through a residency lease. Static routes and other
/// domains therefore never hold a releasable instance: they hold this gate, which loads the module on
/// first use, keeps it alive for the call or transferred stream/enumerator ownership, and lets the
/// idle sweep release it only after that ownership ends.
///
/// <para>
/// The gate validates deferred ownership shapes instead of silently releasing a raw deferred result.
/// Streams must declare Stream; asynchronous sequences declare IAsyncEnumerable directly. A failing load surfaces as
/// <see cref="ModuleUnavailableException"/> before any module code runs.
/// </para>
/// </summary>
public class ModuleServiceProxy<TService> : DispatchProxy where TService : class
{
    private static readonly MethodInfo TaskOfTypeMethod = typeof(ModuleServiceProxy<TService>)
        .GetMethod(nameof(InvokeTaskOfType), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ValueTaskOfTypeMethod = typeof(ModuleServiceProxy<TService>)
        .GetMethod(nameof(InvokeValueTaskOfType), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo EnumerableMethod = typeof(ModuleServiceProxy<TService>)
        .GetMethod(nameof(InvokeEnumerable), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private ModuleResidencyCoordinator _coordinator = null!;
    private string _moduleId = null!;

    internal static TService Create(ModuleResidencyCoordinator coordinator, string moduleId)
    {
        ValidateContract();
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
        var returnType = targetMethod.ReturnType;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
            return EnumerableMethod.MakeGenericMethod(returnType.GetGenericArguments()[0])
                .Invoke(this, [targetMethod, args, cancellationToken, CancellationToken.None]);
        var leaseTask = _coordinator.AcquireAsync(_moduleId, cancellationToken);

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

        ModuleLease? acquired = leaseTask.GetAwaiter().GetResult();
        try
        {
            return ProtectResult(InvokeTarget(targetMethod, args, acquired), returnType, ref acquired);
        }
        finally { acquired?.Dispose(); }
    }

    private async Task InvokeTaskAsync(MethodInfo method, object?[]? args, Task<ModuleLease> leaseTask)
    {
        using var lease = await leaseTask.ConfigureAwait(false);
        await ((Task)InvokeResult(method, args, lease)).ConfigureAwait(false);
    }

    private async Task<TResult> InvokeTaskOfType<TResult>(MethodInfo method, object?[]? args, Task<ModuleLease> leaseTask)
    {
        ModuleLease? lease = await leaseTask.ConfigureAwait(false);
        try
        {
            var result = await ((Task<TResult>)InvokeResult(method, args, lease)).ConfigureAwait(false);
            return (TResult)ProtectResult(result, typeof(TResult), ref lease)!;
        }
        finally { lease?.Dispose(); }
    }

    private async ValueTask InvokeValueTaskAsync(MethodInfo method, object?[]? args, Task<ModuleLease> leaseTask)
    {
        using var lease = await leaseTask.ConfigureAwait(false);
        await ((ValueTask)InvokeResult(method, args, lease)).ConfigureAwait(false);
    }

    private async ValueTask<TResult> InvokeValueTaskOfType<TResult>(MethodInfo method, object?[]? args,
        Task<ModuleLease> leaseTask)
    {
        ModuleLease? lease = await leaseTask.ConfigureAwait(false);
        try
        {
            var result = await ((ValueTask<TResult>)InvokeResult(method, args, lease)).ConfigureAwait(false);
            return (TResult)ProtectResult(result, typeof(TResult), ref lease)!;
        }
        finally { lease?.Dispose(); }
    }

    private static object? ProtectResult(object? result, Type declaredType, ref ModuleLease? lease)
    {
        if (result is not Stream stream) return result;
        if (declaredType != typeof(Stream))
        {
            try { stream.Dispose(); }
            catch { lease!.Quarantine(stream); throw; }
            throw new InvalidOperationException("Module stream results must declare the Stream ownership boundary.");
        }
        var protectedStream = new ModuleLeasedStream(stream, lease!);
        lease = null;
        return protectedStream;
    }

    private async IAsyncEnumerable<T> InvokeEnumerable<T>(MethodInfo method, object?[]? args,
        CancellationToken sourceCancellation, [EnumeratorCancellation] CancellationToken enumerationCancellation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(sourceCancellation, enumerationCancellation);
        using var lease = await _coordinator.AcquireAsync(_moduleId, cancellation.Token).ConfigureAwait(false);
        var actualArgs = args?.ToArray();
        var parameters = method.GetParameters();
        for (var index = 0; actualArgs is not null && index < parameters.Length; index++)
            if (parameters[index].ParameterType == typeof(CancellationToken)) actualArgs[index] = cancellation.Token;
        IAsyncEnumerator<T>? enumerator = null;
        try
        {
            enumerator = ((IAsyncEnumerable<T>)InvokeResult(method, actualArgs, lease)).GetAsyncEnumerator(cancellation.Token);
            while (await enumerator.MoveNextAsync().ConfigureAwait(false)) yield return enumerator.Current;
        }
        finally
        {
            if (enumerator is not null)
            {
                try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                catch { lease.Quarantine(enumerator); throw; }
            }
        }
    }

    private static void ValidateContract()
    {
        if (!typeof(TService).IsInterface) throw new InvalidOperationException("A module gate requires an interface contract.");
        foreach (var method in typeof(TService).GetInterfaces().Append(typeof(TService)).SelectMany(type => type.GetMethods()))
        {
            var result = method.ReturnType;
            var asynchronous = result.IsGenericType && (result.GetGenericTypeDefinition() == typeof(Task<>) || result.GetGenericTypeDefinition() == typeof(ValueTask<>));
            if (asynchronous) result = result.GetGenericArguments()[0];
            var generic = result.IsGenericType ? result.GetGenericTypeDefinition() : null;
            if (result == typeof(object) || typeof(IResult).IsAssignableFrom(result) || typeof(IQueryable).IsAssignableFrom(result)
                || result == typeof(System.Collections.IEnumerable) || method.ReturnType.IsByRef
                || method.GetParameters().Any(parameter => parameter.ParameterType.IsByRef)
                || typeof(Stream).IsAssignableFrom(result) && result != typeof(Stream)
                || asynchronous && generic == typeof(IAsyncEnumerable<>)
                || generic == typeof(IEnumerable<>) || generic == typeof(IEnumerator<>) || generic == typeof(IAsyncEnumerator<>)
                || asynchronous && (result == typeof(ValueTask) || generic == typeof(ValueTask<>))
                || typeof(Task).IsAssignableFrom(result) && result != method.ReturnType)
                throw new InvalidOperationException($"Module service {typeof(TService).Name}.{method.Name} has an unsupported deferred ownership contract.");
        }
    }

    private static object InvokeResult(MethodInfo method, object?[]? args, ModuleLease lease) =>
        InvokeTarget(method, args, lease)
        ?? throw new InvalidOperationException($"{typeof(TService).FullName}.{method.Name} returned no result object.");

    private static object? InvokeTarget(MethodInfo method, object?[]? args, ModuleLease lease)
    {
        try { return method.Invoke(lease.Runtime.Resolve(typeof(TService)), args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

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
