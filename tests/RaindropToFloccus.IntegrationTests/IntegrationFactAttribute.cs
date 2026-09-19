using Xunit;

namespace RaindropToFloccus.IntegrationTests;

public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (!IntegrationTestConfiguration.IsEnabled)
        {
            Skip = "Live account changes require RAINDROP_RUN_INTEGRATION_TESTS=1 and RAINDROP_API_TOKEN.";
        }
    }
}
