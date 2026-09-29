using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caravel.Queues;

public sealed class DatabaseQueueOptions
{
    public int MaxPayloadBytes { get; set; } = 64 * 1024;
    public int MaxAttempts { get; set; } = 3;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan? LeaseRenewalInterval { get; set; }
    public TimeSpan MaxLeaseLifetime { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    internal DatabaseQueueOptions ValidatedCopy()
    {
        if (MaxPayloadBytes is < 1 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxPayloadBytes));
        if (MaxAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        if (LeaseDuration < TimeSpan.FromSeconds(1) || LeaseDuration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
        if (MaxLeaseLifetime < LeaseDuration || MaxLeaseLifetime > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(MaxLeaseLifetime));
        if (LeaseRenewalInterval is { } renewal &&
            (renewal < TimeSpan.FromMilliseconds(100) || renewal > LeaseDuration / 2))
            throw new ArgumentOutOfRangeException(nameof(LeaseRenewalInterval));
        if (RetryDelay < TimeSpan.FromMilliseconds(1) || MaxRetryDelay < RetryDelay || MaxRetryDelay > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        return (DatabaseQueueOptions)MemberwiseClone();
    }
}

public static class QueueServiceExtensions
{
    /// <summary>Register an application-configured IDbContextFactory&lt;QueueDbContext&gt; separately.</summary>
    public static IServiceCollection AddCaravelDatabaseQueue(this IServiceCollection services,
        Action<DatabaseQueueOptions>? configure = null)
    {
        var options = new DatabaseQueueOptions();
        configure?.Invoke(options);
        services.AddSingleton(options.ValidatedCopy());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<JobRegistry>();
        services.TryAddSingleton<DatabaseQueue>();
        services.TryAddSingleton<IDatabaseQueue>(provider => provider.GetRequiredService<DatabaseQueue>());
        services.TryAddSingleton(provider => new QueueWorker(provider.GetRequiredService<IDatabaseQueue>(),
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<JobRegistry>(),
            provider.GetRequiredService<DatabaseQueueOptions>(), provider.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>Use a stable versioned wire name, such as reports.generate.v1. Never use a CLR type name from input.</summary>
    public static IServiceCollection AddQueueJob<TJob, THandler>(this IServiceCollection services, string name)
        where TJob : notnull where THandler : class, IJobHandler<TJob>
    {
        QueueValidation.Name(name, 128, nameof(name));
        if (services.Any(x => x.ServiceType == typeof(JobRegistration) && x.ImplementationInstance is JobRegistration entry
            && (entry.Name == name || entry.JobType == typeof(TJob))))
            throw new InvalidOperationException("Each queue job type and wire name must be registered once.");
        services.AddScoped<THandler>();
        services.AddSingleton(new JobRegistration(name, typeof(TJob), async (provider, payload, context, cancellationToken) =>
        {
            TJob job;
            try
            {
                job = JsonSerializer.Deserialize<TJob>(payload, JobRegistry.Json)
                    ?? throw new JsonException("Job payload cannot be null.");
            }
            catch (JsonException) { return QueueFailure.InvalidPayload; }
            await provider.GetRequiredService<THandler>().HandleAsync(job, context, cancellationToken).ConfigureAwait(false);
            return null;
        }));
        return services;
    }

    /// <summary>Opt into one sequential polling worker. Use multiple service instances for bounded parallelism.</summary>
    public static IServiceCollection AddCaravelQueueWorker(this IServiceCollection services, string queue,
        TimeSpan? pollInterval = null)
    {
        QueueValidation.Name(queue, 64, nameof(queue));
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        if (interval < TimeSpan.FromMilliseconds(100) || interval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider =>
            new QueueHostedService(provider.GetRequiredService<QueueWorker>(), queue, interval,
                provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}

internal sealed record JobRegistration(string Name, Type JobType,
    Func<IServiceProvider, string, JobContext, CancellationToken, Task<QueueFailure?>> InvokeAsync);

internal sealed class JobRegistry(IEnumerable<JobRegistration> registrations)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private readonly Dictionary<Type, JobRegistration> byType = registrations.ToDictionary(x => x.JobType);
    private readonly Dictionary<string, JobRegistration> byName = registrations.ToDictionary(x => x.Name, StringComparer.Ordinal);
    public JobRegistration For<TJob>() => byType.TryGetValue(typeof(TJob), out var job) ? job
        : throw new InvalidOperationException("The queue job type is not registered.");
    public JobRegistration? Find(string name) => byName.GetValueOrDefault(name);
}

internal static class QueueValidation
{
    public static void Name(string value, int length, string argument)
    {
        Text(value, length, argument);
        if (value.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_')))
            throw new ArgumentException("Use lowercase letters, digits, dots, hyphens or underscores.", argument);
    }

    public static void Text(string value, int length, string argument)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > length || value.Any(char.IsControl))
            throw new ArgumentException($"A nonblank value of at most {length} characters without control characters is required.", argument);
    }
}
