internal sealed class IndependentManagedRuntimeTransport : IPrivilegedOperationTransport
{
    private readonly Dictionary<string, ManagedProcessSnapshot> _states = new();
    public ManagedProcessSnapshot FrpsState => _states.GetValueOrDefault("frps") ?? new(false, false, false, null, []);
    public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
    {
        var runtime = request.ManagedRuntime ?? throw new InvalidOperationException();
        var key = runtime.Runtime == ManagedRuntime.Frps ? "frps" : runtime.ProfileId?.ToString("N") ?? "runtime";
        if (runtime.Action == ManagedRuntimeAction.Start)
            _states[key] = new(true, true, false, DateTimeOffset.UtcNow, [new(DateTimeOffset.UtcNow, "information", "FRP connected.")], runtime.AppliedIdentity);
        if (runtime.Action == ManagedRuntimeAction.Stop) _states.Remove(key);
        if (runtime.Action == ManagedRuntimeAction.Uninstall) _states.Clear();
        return Task.FromResult(new PrivilegedOperationResult(true, ComponentProcess: _states.GetValueOrDefault(key) ?? new(false, false, false, null, [])));
    }
}
