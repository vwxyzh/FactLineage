using FactLineage.Cloud.Api.Application;

namespace FactLineage.Cloud.Api.Infrastructure;

public sealed class CloudInitializer(IMemoryRepository repository, IMemorySearchIndex searchIndex) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await repository.InitializeAsync(cancellationToken);
        await searchIndex.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}