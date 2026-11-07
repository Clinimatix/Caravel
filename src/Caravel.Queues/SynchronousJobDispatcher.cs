using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caravel.Queues;

/// <summary>Explicit immediate job execution for development and tests; no durability, retries or deduplication.</summary>
public sealed class SynchronousJobDispatcher
{
    private readonly IServiceScopeFactory scopes;
    private readonly JobRegistry registry;
    internal SynchronousJobDispatcher(IServiceScopeFactory scopes, JobRegistry registry)
        => (this.scopes, this.registry) = (scopes, registry);

    /// <summary>Runs once in a fresh scope. The caller must authorize the tenant; failures propagate.</summary>
    public async Task DispatchAsync<TJob>(TJob job, string tenantId, CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(job);
        QueueValidation.Text(tenantId, 128, nameof(tenantId));
        var registration = registry.For<TJob>();
        var payload = JsonSerializer.Serialize(job, JobRegistry.Json);
        await using var scope = scopes.CreateAsyncScope();
        var failure = await registration.InvokeAsync(scope.ServiceProvider, payload,
            new JobContext(Guid.NewGuid(), "sync", tenantId, 1), cancellationToken).ConfigureAwait(false);
        if (failure is not null) throw new InvalidOperationException("The synchronous job payload could not be read.");
    }
}

public static class SynchronousJobServices
{
    /// <summary>Opt into immediate registered jobs without registering a database queue or hosted worker.</summary>
    public static IServiceCollection AddCaravelSynchronousJobs(this IServiceCollection services)
    {
        services.TryAddSingleton<JobRegistry>();
        services.TryAddSingleton(provider => new SynchronousJobDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<JobRegistry>()));
        return services;
    }
}
