using RelaxKonOS.Protocol.Observability;

namespace RelaxKonOS.Server.Observability;

public interface ICorrelationContextAccessor
{
    CorrelationContext? Current { get; }
    IDisposable Push(CorrelationContext context);
}

public sealed class CorrelationContextAccessor : ICorrelationContextAccessor
{
    private static readonly AsyncLocal<CorrelationContext?> CurrentContext = new();
    public CorrelationContext? Current => CurrentContext.Value;
    public IDisposable Push(CorrelationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Restore(previous);
    }
    private sealed class Restore(CorrelationContext? previous) : IDisposable
    {
        public void Dispose() => CurrentContext.Value = previous;
    }
}
