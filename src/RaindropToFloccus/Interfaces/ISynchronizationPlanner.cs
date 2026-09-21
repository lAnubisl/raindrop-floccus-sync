namespace RaindropToFloccus.Interfaces;

public interface ISynchronizationPlanner
{
    SynchronizationPlan CreatePlan(SynchronizationComparison comparison, XbelDocument source);
}
