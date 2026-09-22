using System;
using System.Collections.Generic;
using System.Threading;

namespace RelaxKonOS.Server.SystemPerformance;

/// <summary>
/// 记录当前谁需要采样：已订阅的 PerformanceHub 连接，以及一次性 REST 读取申请的短期 demand 租约。
/// 两者都为空时采样器必须完全空闲（成本规则：没有消费者就没有 1 Hz 的 OS 读取）。
/// </summary>
public sealed class PerformanceSubscriptionRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<string> _connections = new(StringComparer.Ordinal);
    private int _demands;

    /// <summary>是否仍有消费者需要样本：存在订阅连接，或存在未释放的 demand 租约。</summary>
    public bool NeedsCollection
    {
        get { lock (_gate) return NeedsCollectionLocked(); }
    }

    /// <summary>需求从无到有或从有到无时触发一次；参数为触发之后的状态。</summary>
    public event Action<bool>? CollectionNeededChanged;

    public void Subscribe(string connectionId)
    {
        bool becameNeeded;
        lock (_gate)
        {
            becameNeeded = !NeedsCollectionLocked() && _connections.Add(connectionId);
        }
        if (becameNeeded) CollectionNeededChanged?.Invoke(true);
    }

    public void Unsubscribe(string connectionId)
    {
        bool becameIdle;
        lock (_gate)
        {
            becameIdle = _connections.Remove(connectionId) && !NeedsCollectionLocked();
        }
        if (becameIdle) CollectionNeededChanged?.Invoke(false);
    }

    /// <summary>
    /// 为一次一次性读取申请有界采样窗口。调用方应在拿到答案后立即释放；无论如何 <paramref name="window"/>
    /// 之后租约都会自动释放——一个卡住的请求不能把 1 Hz 采样永久钉在这台机器上。
    /// </summary>
    public IDisposable RequestCollection(TimeSpan window)
    {
        bool becameNeeded;
        lock (_gate)
        {
            var needed = NeedsCollectionLocked();
            becameNeeded = ++_demands == 1 && !needed;
        }
        if (becameNeeded) CollectionNeededChanged?.Invoke(true);
        return new DemandLease(this, window);
    }

    private bool NeedsCollectionLocked() => _connections.Count != 0 || _demands != 0;

    private void ReleaseDemand()
    {
        bool becameIdle;
        lock (_gate)
        {
            if (_demands == 0) return;
            becameIdle = --_demands == 0 && !NeedsCollectionLocked();
        }
        if (becameIdle) CollectionNeededChanged?.Invoke(false);
    }

    /// <summary>单次 demand。重复释放是安全的，也不会多减计数。</summary>
    private sealed class DemandLease : IDisposable
    {
        private readonly PerformanceSubscriptionRegistry _registry;
        private readonly Timer _expiry;
        private int _released;

        public DemandLease(PerformanceSubscriptionRegistry registry, TimeSpan window)
        {
            _registry = registry;
            // 计时器先建为不触发，回调才不会早于字段赋值运行。
            _expiry = new Timer(_ => Release(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _expiry.Change(window, Timeout.InfiniteTimeSpan);
        }

        public void Dispose() => Release();

        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _expiry.Dispose();
            _registry.ReleaseDemand();
        }
    }
}
