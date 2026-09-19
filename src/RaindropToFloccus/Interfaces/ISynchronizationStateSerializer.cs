using RaindropToFloccus.Models;

namespace RaindropToFloccus.Interfaces;

public interface ISynchronizationStateSerializer
{
    int CurrentSchemaVersion { get; }

    void Validate(SynchronizationState state);

    SynchronizationState Parse(string content);

    string Serialize(SynchronizationState state);
}
