using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Caravel.Core;

/// <summary>Registers services first, then boots once before the host accepts requests.</summary>
public abstract class ServiceProvider
{
    public virtual IReadOnlyList<Type> Dependencies => [];
    public virtual void Register(IServiceCollection services) { }
    public virtual ValueTask BootAsync(CaravelApplication app, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

public sealed record CaravelApplication(
    IServiceProvider Services, IConfiguration Configuration, IHostEnvironment Environment);

public sealed class CaravelOptions
{
    internal List<ServiceProvider> Providers { get; } = [];
    public string EnvironmentFile { get; set; } = ".env";

    public CaravelOptions AddProvider<T>() where T : ServiceProvider, new()
    {
        if (Providers.Any(provider => provider.GetType() == typeof(T)))
            throw new InvalidOperationException($"Provider {typeof(T).FullName} is already registered.");
        Providers.Add(new T());
        return this;
    }

    internal IReadOnlyList<ServiceProvider> OrderedProviders()
    {
        var ordered = new List<ServiceProvider>();
        var visiting = new HashSet<Type>();
        var visited = new HashSet<Type>();
        foreach (var provider in Providers) Visit(provider);
        return ordered;

        void Visit(ServiceProvider provider)
        {
            var type = provider.GetType();
            if (visited.Contains(type)) return;
            if (!visiting.Add(type))
                throw new InvalidOperationException($"Circular service provider dependency involving {type.FullName}.");
            foreach (var dependency in provider.Dependencies)
                Visit(Providers.SingleOrDefault(candidate => candidate.GetType() == dependency)
                    ?? throw new InvalidOperationException($"Provider {type.FullName} requires unregistered provider {dependency.FullName}."));
            visiting.Remove(type);
            visited.Add(type);
            ordered.Add(provider);
        }
    }
}

public static class CaravelServiceExtensions
{
    public static IServiceCollection AddCaravelProviders(this IServiceCollection services, CaravelOptions options)
    {
        if (services.Any(service => service.ServiceType == typeof(CaravelProviderLifecycle)))
            throw new InvalidOperationException("Clinimatix Caravel is already registered.");
        var providers = options.OrderedProviders();
        foreach (var provider in providers) provider.Register(services);
        services.AddSingleton(provider => new CaravelProviderLifecycle(providers,
            new CaravelApplication(provider, provider.GetRequiredService<IConfiguration>(),
                provider.GetRequiredService<IHostEnvironment>())));
        // Insert before application hosted services so their startup observes completed provider boot.
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(
            provider => provider.GetRequiredService<CaravelProviderLifecycle>()));
        return services;
    }
}

public sealed class CaravelProviderLifecycle(
    IReadOnlyList<ServiceProvider> providers, CaravelApplication application) : IHostedLifecycleService
{
    private readonly object gate = new();
    private Task? boot;

    // The host completes every StartingAsync before any StartAsync, even with concurrent service startup.
    public Task StartingAsync(CancellationToken cancellationToken) => StartAsync(cancellationToken);
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (gate) return boot ??= BootAsync(cancellationToken);
    }

    private async Task BootAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await provider.BootAsync(application, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
