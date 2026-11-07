using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Proxy;
using RelaxKonOS.Protocol.Proxy;

internal static class ProxyTrafficPollingChecks
{
    internal static void Run(Window host)
    {
        var repository = DispatchProxy.Create<IProxyRepository, TrafficRepository>();
        var transport = (TrafficRepository)(object)repository;
        var model = new ProxyManagerViewModel(repository, false, false);
        var viewType = typeof(ProxyManagerViewModel).Assembly.GetType("RelaxKonOS.Client.Apps.Proxy.Views.ProxyOverviewView", throwOnError: true)!;

        host.Content = NewOverview();
        PumpUntil(() => transport.Requests.Count == 1, "The overview did not start polling on attach.");
        var first = transport.Requests[0];
        var elapsed = Stopwatch.StartNew();
        PumpUntil(() => elapsed.Elapsed >= TimeSpan.FromMilliseconds(2300), "The polling interval did not elapse.");
        if (transport.Requests.Count != 1) throw new InvalidOperationException("A slow request overlapped a timer tick.");

        host.Content = null;
        PumpUntil(() => first.Cancellation.IsCancellationRequested, "Detaching the overview did not cancel its request.");
        host.Content = NewOverview();
        Dispatcher.UIThread.RunJobs();
        if (transport.Requests.Count != 1) throw new InvalidOperationException("Reattaching started a request while the original was still draining.");

        first.Reply.SetResult(new(1, 1, 1, 1, 1));
        PumpUntil(() => transport.Requests.Count == 2, "The new overview did not resume polling after the original request ended.");
        if (model.Traffic is not null) throw new InvalidOperationException("A detached request updated the new overview.");
        transport.Requests[1].Reply.SetResult(new(2, 2, 2, 2, 2));
        PumpUntil(() => model.Traffic is { UploadBytesPerSecond: 2 }, "The resumed poll did not update traffic.");
        host.Content = null;
        Console.WriteLine("PASS PROXY OVERVIEW POLLING: slow tick skipped, detach cancelled, reattach waited, late sample ignored");

        Control NewOverview()
        {
            var view = (Control)Activator.CreateInstance(viewType)!;
            view.DataContext = model;
            return view;
        }
    }

    private static void PumpUntil(Func<bool> condition, string error)
    {
        if (condition()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        pulse.Tick += (_, _) => { if (condition()) deadline.Cancel(); };
        pulse.Start();
        try { Dispatcher.UIThread.MainLoop(deadline.Token); }
        finally { pulse.Stop(); }
        if (!condition()) throw new InvalidOperationException(error);
    }

}

public class TrafficRepository : DispatchProxy
{
    internal List<(CancellationToken Cancellation, TaskCompletionSource<ProxyTrafficDto> Reply)> Requests { get; } = [];

    protected override object Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != nameof(IProxyRepository.GetTrafficAsync)) throw new NotSupportedException(targetMethod?.Name);
        var reply = new TaskCompletionSource<ProxyTrafficDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        Requests.Add(((CancellationToken)args![0]!, reply));
        return reply.Task;
    }
}
