using System.Collections.Concurrent;

namespace CodexPhoneReminder.Agent;

public sealed class ProgressSubscriptionService
{
    private readonly ConcurrentDictionary<string, Lease> _leases = [];
    private long _generation;

    public ProgressLease Renew(string taskId)
    {
        var now = DateTimeOffset.UtcNow;
        var lease = _leases.AddOrUpdate(taskId,
            _ => new(now.AddSeconds(8), Interlocked.Increment(ref _generation)),
            (_, current) => current.ExpiresAt <= now
                ? new(now.AddSeconds(8), Interlocked.Increment(ref _generation))
                : current with { ExpiresAt = now.AddSeconds(8) });
        return new(true, lease.ExpiresAt, lease.Generation);
    }

    public bool IsActive(string taskId) => _leases.TryGetValue(taskId, out var lease) && lease.ExpiresAt > DateTimeOffset.UtcNow;
    public long Generation(string taskId) => IsActive(taskId) && _leases.TryGetValue(taskId, out var lease) ? lease.Generation : 0;
    private sealed record Lease(DateTimeOffset ExpiresAt, long Generation);
}

public sealed record ProgressLease(bool Active, DateTimeOffset ExpiresAt, long Generation);
