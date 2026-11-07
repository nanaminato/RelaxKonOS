using System.Text.Json;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Server.Certificate;

/// <summary>Durable scan boundaries; outcome counts are read from the operation ledger.</summary>
internal sealed class CertificateRenewalRunRepository(IHostEnvironment environment)
{
    private readonly string _path = Path.Combine(environment.ContentRootPath, "data", "certificate-renewal-runs.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record Run(Guid Id, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, Guid[] Operations, int Rejected,
        bool Scheduled = false, Guid[]? RejectedCertificates = null);
    internal sealed record Slot(Guid Id, IReadOnlyList<Guid> Operations, IReadOnlyList<Guid> RejectedCertificates)
    {
        internal string RequestKey => $"background-scan-{Id:N}";
    }

    internal async Task<DateTimeOffset?> NextScanAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return Read().LastOrDefault(run => run.Scheduled)?.CompletedAt?.AddDays(1); }
        finally { _gate.Release(); }
    }

    internal async Task<Slot?> BeginDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var runs = Read();
            var latest = runs.LastOrDefault(run => run.Scheduled);
            if (latest is { CompletedAt: null }) return new(latest.Id, latest.Operations, latest.RejectedCertificates ?? []);
            if (latest?.CompletedAt?.AddDays(1) > now) return null;
            var run = new Run(Guid.NewGuid(), now, null, [], 0, Scheduled: true);
            runs.Add(run);
            await SaveAsync(runs, ct);
            return new(run.Id, run.Operations, []);
        }
        finally { _gate.Release(); }
    }

    internal Task AttachAsync(Guid id, IReadOnlyCollection<Guid> operationIds, CancellationToken ct) => UpdateAsync(runs =>
    {
        var index = runs.FindIndex(r => r.Id == id);
        var run = runs[index];
        runs[index] = run with { Operations = run.Operations.Concat(operationIds).Distinct().ToArray() };
    }, ct);

    public Task AddAsync(Guid id, Guid certificateId, Guid operationId, CancellationToken ct) => UpdateAsync(runs =>
    {
        var index = runs.FindIndex(r => r.Id == id);
        var run = runs[index];
        runs[index] = operationId == Guid.Empty ? run with { RejectedCertificates = (run.RejectedCertificates ?? []).Append(certificateId).Distinct().ToArray() }
            : run with { Operations = run.Operations.Append(operationId).Distinct().ToArray() };
    }, ct);
    public Task EndAsync(Guid id, DateTimeOffset now, CancellationToken ct) => UpdateAsync(runs =>
    {
        var index = runs.FindIndex(r => r.Id == id);
        runs[index] = runs[index] with { CompletedAt = now };
    }, ct);

    public async Task<IReadOnlyList<CertificateRenewalRunDto>> ListAsync(CertificateOperationStore operations, CancellationToken ct)
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
                run.Rejected + (run.RejectedCertificates?.Length ?? 0) + outcomes.Count(o => o.State == CertificateOperationState.Failed), pending,
                outcomes.Count(o => o.State == CertificateOperationState.Cancelled)));
        }
        return result.OrderByDescending(r => r.StartedAt).ToArray();
    }
    private List<Run> Read()
    {
        try
        {
            return JsonSerializer.Deserialize<List<Run>>(File.ReadAllText(_path)) ?? throw new JsonException("Renewal runs must be an array.");
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }
    private async Task UpdateAsync(Action<List<Run>> update, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var runs = Read();
            update(runs);
            await SaveAsync(runs, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task SaveAsync(List<Run> runs, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(output, runs, cancellationToken: ct);
            output.Flush(flushToDisk: true);
        }
        File.Move(temp, _path, true);
    }
}
