namespace RelaxKonOS.Server.Certificate;

/// <summary>Anvil calls without cancellation support remain owned until their actual task ends.</summary>
public static class AcmeCallLifetime
{
    public static async Task<T> AwaitAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await operation();
        ct.ThrowIfCancellationRequested();
        return result;
    }
    public static async Task AwaitAsync(Func<Task> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await operation();
        ct.ThrowIfCancellationRequested();
    }
}
