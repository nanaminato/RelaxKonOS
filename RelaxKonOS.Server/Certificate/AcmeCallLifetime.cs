namespace RelaxKonOS.Server.Certificate;

/// <summary>Anvil calls without cancellation support remain owned until their actual task ends.</summary>
internal static class AcmeCallLifetime
{
    internal static async Task<T> AwaitAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await operation();
        ct.ThrowIfCancellationRequested();
        return result;
    }
    internal static async Task AwaitAsync(Func<Task> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await operation();
        ct.ThrowIfCancellationRequested();
    }
}
