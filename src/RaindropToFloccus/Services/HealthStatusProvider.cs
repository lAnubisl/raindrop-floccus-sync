using RaindropToFloccus.Interfaces;
using RaindropToFloccus.Models;

namespace RaindropToFloccus.Services;

public sealed class HealthStatusProvider : IHealthStatusProvider
{
    private int _currentStatus = (int)SynchronizationHealthStatus.Starting;

    public SynchronizationHealthStatus CurrentStatus =>
        (SynchronizationHealthStatus)Volatile.Read(ref _currentStatus);

    public void MarkHealthy()
    {
        Interlocked.Exchange(ref _currentStatus, (int)SynchronizationHealthStatus.Healthy);
    }

    public void MarkDegraded()
    {
        Interlocked.Exchange(ref _currentStatus, (int)SynchronizationHealthStatus.Degraded);
    }
}
