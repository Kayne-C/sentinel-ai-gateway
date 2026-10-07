namespace Sentinel.Infrastructure.Audit;

/// <summary>
/// Asynchronous mutual exclusion per key (one <see cref="SemaphoreSlim"/> per tenant). Gates are reference counted
/// and removed when their last holder or waiter leaves, so the table is as large as the number of tenants appending
/// right now, not every tenant the process has ever seen.
/// </summary>
internal sealed class KeyedAsyncLock
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);

    /// <summary>Number of keys currently held or awaited.</summary>
    public int ActiveKeys
    {
        get
        {
            lock (_sync)
            {
                return _gates.Count;
            }
        }
    }

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        Gate? gate;
        lock (_sync)
        {
            if (!_gates.TryGetValue(key, out gate))
            {
                gate = new Gate();
                _gates.Add(key, gate);
            }

            // Counted before waiting: a gate with waiters is never removed underneath them.
            gate.Users++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(key, gate);
            throw;
        }

        return new Lease(this, key, gate);
    }

    private void Leave(string key, Gate gate)
    {
        lock (_sync)
        {
            if (--gate.Users == 0)
            {
                _gates.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Lease(KeyedAsyncLock owner, string key, Gate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Semaphore.Release();
                owner.Leave(key, gate);
            }
        }
    }
}
