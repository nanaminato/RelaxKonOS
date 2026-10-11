using RelaxKonOS.Client.Apps.TaskManager;
using RelaxKonOS.Protocol.SystemMonitor;

var success = new KillProcessResultDto(true, false, string.Empty, null);
var denied = new KillProcessResultDto(false, true, "process.permission_denied", null);
var attempts = new List<bool>();
var grants = 0;
var current = true;
Task<KillProcessResultDto> Terminate(bool elevated)
{
    attempts.Add(elevated);
    return Task.FromResult(elevated ? success : denied);
}
Task<KillProcessResultDto> Elevate(Func<Task<KillProcessResultDto>> operation)
{
    grants++;
    return operation();
}
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
void Reset() { attempts.Clear(); grants = 0; current = true; }

var result = await ProcessTerminationFlow.ExecuteAsync(false, Terminate, Elevate, () => current);
Check(result.Success && attempts.SequenceEqual(new[] { false, true }) && grants == 1,
    "Ordinary permission denial must escalate exactly once.");
Reset();
result = await ProcessTerminationFlow.ExecuteAsync(true, Terminate, Elevate, () => current);
Check(result.Success && attempts.SequenceEqual(new[] { true }) && grants == 1,
    "Administrator termination must pass through the grant broker.");

foreach (var receipt in new[] { success, new KillProcessResultDto(false, false, "process.instance_changed", null),
    new KillProcessResultDto(false, false, "process.termination_unverified", null) })
{
    Reset();
    result = await ProcessTerminationFlow.ExecuteAsync(false, elevated =>
    {
        attempts.Add(elevated);
        return Task.FromResult(receipt);
    }, Elevate, () => current);
    Check(result == receipt && attempts.SequenceEqual(new[] { false }) && grants == 0,
        "Success or a non-permission failure must not escalate.");
}

Reset();
try
{
    await ProcessTerminationFlow.ExecuteAsync(false, elevated =>
    {
        attempts.Add(elevated);
        throw new HttpRequestException("Unknown termination outcome");
    }, Elevate, () => current);
    throw new InvalidOperationException("Expected transport failure.");
}
catch (HttpRequestException) { }
Check(attempts.SequenceEqual(new[] { false }) && grants == 0, "Transport failures must never replay termination.");

Reset();
try
{
    await ProcessTerminationFlow.ExecuteAsync(false, elevated =>
    {
        attempts.Add(elevated);
        if (elevated) throw new HttpRequestException("Unknown elevated termination outcome");
        return Task.FromResult(denied);
    }, Elevate, () => current);
    throw new InvalidOperationException("Expected elevated transport failure.");
}
catch (HttpRequestException) { }
Check(attempts.SequenceEqual(new[] { false, true }) && grants == 1,
    "An unknown elevated result must not cause a second elevated termination.");

Reset();
try
{
    await ProcessTerminationFlow.ExecuteAsync(false, Terminate, operation =>
    {
        current = false; // The user changed session while the elevation dialog was open.
        return operation();
    }, () => current);
    throw new InvalidOperationException("Expected stale session cancellation.");
}
catch (OperationCanceledException) { }
Check(attempts.SequenceEqual(new[] { false }), "A session change must prevent the elevated attempt.");

Reset();
try
{
    await ProcessTerminationFlow.ExecuteAsync(false, Terminate, _ => throw new OperationCanceledException(), () => current);
    throw new InvalidOperationException("Expected elevation cancellation.");
}
catch (OperationCanceledException) { }
Check(attempts.SequenceEqual(new[] { false }), "Cancelled elevation must not terminate again.");

Reset();
result = await ProcessTerminationFlow.ExecuteAsync(false, elevated =>
{
    attempts.Add(elevated);
    return Task.FromResult(denied);
}, Elevate, () => current);
Check(result == denied && attempts.SequenceEqual(new[] { false, true }) && grants == 1,
    "Elevated permission failure must not loop.");

Console.WriteLine("Task manager termination checks passed (10 scenarios).");
