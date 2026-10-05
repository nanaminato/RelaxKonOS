using System.Text.Json;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Server.Certificate;

/// <summary>Durable scan boundaries; outcome counts are read from the operation ledger.</summary>
internal sealed class CertificateRenewalRunRepository(IHostEnvironment environment, CertificateOperationStore operations)
{
    private readonly string _path = Path.Combine(environment.ContentRootPath, "data", "certificate-renewal-runs.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record Run(Guid Id, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, Guid[] Operations, int Rejected);

    public async Task<Guid> BeginAsync(CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await UpdateAsync(runs => runs.Add(new(id, DateTimeOffset.UtcNow, null, [], 0)), ct);
        return id;
    }
    public Task AddAsync(Guid id, Guid operationId, CancellationToken ct) => UpdateAsync(runs =>
    {
        var index = runs.FindIndex(r => r.Id == id);
        var run = runs[index];
        runs[index] = operationId == Guid.Empty ? run with { Rejected = run.Rejected + 1 }
            : run with { Operations = run.Operations.Append(operationId).Distinct().ToArray() };
    }, ct);
    public Task EndAsync(Guid id, CancellationToken ct) => UpdateAsync(runs =>
    {
        var index = runs.FindIndex(r => r.Id == id);
        runs[index] = runs[index] with { CompletedAt = DateTimeOffset.UtcNow };
    }, ct);

    public async Task<IReadOnlyList<CertificateRenewalRunDto>> ListAsync(CancellationToken ct)
    {
        List<Run> runs;
        await _gate.WaitAsync(ct);
        try { runs = Read(); }
        finally { _gate.Release(); }
        var result = new List<CertificateRenewalRunDto>(await operations.GetManualRenewalRunsAsync(ct));
        foreach (var run in runs)
        {
            var outcomes = new List<CertificateOperationDto>();
            foreach (var id in run.Operations)
                if (await operations.GetAsync(id, ct) is { } operation) outcomes.Add(operation);
            var pending = outcomes.Count(o => o.State is CertificateOperationState.Queued or CertificateOperationState.Running);
            result.Add(new(run.Id, run.StartedAt, pending == 0 && run.CompletedAt is { } end ? outcomes.Select(o => o.CompletedAt ?? end).Append(end).Max() : null, true,
                outcomes.Count(o => o.State == CertificateOperationState.Succeeded),
                run.Rejected + outcomes.Count(o => o.State == CertificateOperationState.Failed), pending,
                outcomes.Count(o => o.State == CertificateOperationState.Cancelled)));
        }
        return result.OrderByDescending(r => r.StartedAt).ToArray();
    }
    private List<Run> Read() => File.Exists(_path)
        ? JsonSerializer.Deserialize<List<Run>>(File.ReadAllText(_path)) ?? [] : [];
    private async Task UpdateAsync(Action<List<Run>> update, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var runs = Read();
            update(runs);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(_path + ".tmp", JsonSerializer.Serialize(runs), ct);
            File.Move(_path + ".tmp", _path, true);
        }
        finally { _gate.Release(); }
    }
}
