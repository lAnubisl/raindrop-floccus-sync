using RaindropToFloccus.Models;

namespace RaindropToFloccus.Interfaces;

public interface IHealthStatusProvider
{
    SynchronizationHealthStatus CurrentStatus { get; }

    void MarkHealthy();

    void MarkDegraded();
}

