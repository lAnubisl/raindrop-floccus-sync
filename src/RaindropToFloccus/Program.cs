using RaindropToFloccus.Clients;
using RaindropToFloccus.Helpers;
using RaindropToFloccus.Interfaces;
using RaindropToFloccus.Models;
using RaindropToFloccus.Services;

try
{
    var configurationProvider = new EnvironmentConfigurationHelper();
    var builder = WebApplication.CreateSlimBuilder(args);

    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls($"http://0.0.0.0:{configurationProvider.HealthCheckPort}");
    builder.Services.AddSingleton<RaindropToFloccus.Interfaces.IConfigurationProvider>(configurationProvider);
    builder.Services.AddSingleton<RaindropToFloccus.Interfaces.ILogger, StandardOutputLoggingHelper>();
    builder.Services.AddSingleton<ICommandRunner, CommandRunner>();
    builder.Services.AddSingleton<IGitSshEnvironmentProvider, GitSshEnvironmentHelper>();
    builder.Services.AddSingleton<IGitRepositoryClient, GiteaSshGitClient>();
    builder.Services.AddSingleton<IXbelDocumentSerializer, FloccusXbelSerializer>();
    builder.Services.AddSingleton<ISynchronizationStateSerializer, VersionedSynchronizationStateSerializer>();
    builder.Services.AddSingleton<ISynchronizationFileStore, RepositorySynchronizationFileStore>();
    builder.Services.AddHttpClient(RaindropApiClient.HttpClientName, client =>
    {
        client.Timeout = configurationProvider.RaindropRequestTimeout;
        client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
    })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .RemoveAllLoggers();
    builder.Services.AddSingleton<IRaindropClient, RaindropApiClient>();
    builder.Services.AddSingleton<IInitialSynchronizationService, GitToRaindropInitializationService>();
    builder.Services.AddSingleton<ISynchronizationComparer, ThreeWaySynchronizationComparer>();
    builder.Services.AddSingleton<ISynchronizationPlanner, GitPrioritySynchronizationPlanner>();
    builder.Services.AddSingleton<ISynchronizationJournalStore, LocalSynchronizationJournalStore>();
    builder.Services.AddSingleton<ISynchronizationService, BidirectionalSynchronizationService>();
    builder.Services.AddSingleton<IHealthStatusProvider, HealthStatusProvider>();
    builder.Services.AddSingleton<ISynchronizationScheduler, SynchronizationScheduler>();
    builder.Services.AddHostedService<Worker>();

    var application = builder.Build();
    application.MapGet(
        "/health",
        HandleHealthRequest);

    await application.RunAsync();
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Fatal startup error: {exception.Message}");
    return 1;
}

static IResult HandleHealthRequest(IHealthStatusProvider healthStatusProvider) => Results.Text(
    healthStatusProvider.CurrentStatus switch
    {
        SynchronizationHealthStatus.Starting => "starting",
        SynchronizationHealthStatus.Healthy => "healthy",
        SynchronizationHealthStatus.Degraded => "degraded",
        _ => throw new InvalidOperationException("Unknown health status.")
    },
    "text/plain");
