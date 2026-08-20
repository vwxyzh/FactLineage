using AiDoc.Cloud.Api.Application;

namespace AiDoc.Cloud.Api.Infrastructure;

public sealed class CloudInitializer(IMemoryRepository repository, IMemorySearchIndex searchIndex) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await repository.InitializeAsync(cancellationToken);
        await searchIndex.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}