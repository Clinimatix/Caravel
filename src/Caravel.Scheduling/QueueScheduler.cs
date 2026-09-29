using System.Globalization;
using System.Text.Json;
using Caravel.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Caravel.Scheduling;

/// <summary>Enqueues the current UTC slot of each registered schedule; missed slots are not backfilled.</summary>
public sealed class QueueScheduler
{
    private readonly IDatabaseQueue queue;
    private readonly ScheduleRegistration[] schedules;
    private readonly TimeProvider clock;

    internal QueueScheduler(IDatabaseQueue queue, IEnumerable<ScheduleRegistration> schedules, TimeProvider clock)
        => (this.queue, this.schedules, this.clock) = (queue, schedules.ToArray(), clock);

    /// <summary>Returns the number of newly enqueued jobs. Persisted queue keys deduplicate repeated or competing polls.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var enqueued = 0;
        foreach (var schedule in schedules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = Math.DivRem(now, schedule.IntervalMilliseconds, out var remainder);
            if (remainder < 0) slot--; // Floor also preserves alignment for instants before the Unix epoch.
            var key = string.Create(CultureInfo.InvariantCulture,
                $"schedule:{schedule.Name}:{schedule.IntervalMilliseconds}:{slot}");
            var result = await schedule.Enqueue(queue,
                new(schedule.Queue, schedule.TenantId, key), cancellationToken).ConfigureAwait(false);
            if (!result.AlreadyEnqueued) enqueued++;
        }
        return enqueued;
    }
}

public static class SchedulingServiceExtensions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };

    /// <summary>Snapshots a typed job for a named schedule. Queue, job handler and schema are configured separately.</summary>
    public static IServiceCollection AddCaravelSchedule<TJob>(this IServiceCollection services, string name,
        TimeSpan interval, TJob job, string queue, string tenantId) where TJob : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(job);
        Name(name, nameof(name));
        Name(queue, nameof(queue));
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 128 || tenantId.Any(char.IsControl))
            throw new ArgumentException("A trusted nonblank tenant ID of at most 128 characters without controls is required.", nameof(tenantId));
        if (interval < TimeSpan.FromSeconds(1) || interval > TimeSpan.FromDays(365) || interval.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new ArgumentOutOfRangeException(nameof(interval), "Use a whole-millisecond interval from one second through 365 days.");
        if (services.Any(service => service.ServiceType == typeof(ScheduleRegistration) &&
                service.ImplementationInstance is ScheduleRegistration registered && registered.Name == name))
            throw new InvalidOperationException("Each schedule name must be registered once.");

        var snapshot = JsonSerializer.SerializeToUtf8Bytes(job, Json);
        // Configuration is trusted; the queue enforces its potentially smaller configured limit at enqueue time.
        if (snapshot.Length > 1024 * 1024) throw new ArgumentException("The scheduled payload exceeds the queue's maximum supported size.", nameof(job));
        services.AddSingleton(new ScheduleRegistration(name, interval.Ticks / TimeSpan.TicksPerMillisecond, queue, tenantId,
            (database, dispatch, token) => database.EnqueueAsync(
                JsonSerializer.Deserialize<TJob>(snapshot, Json) ?? throw new JsonException("A scheduled payload cannot deserialize to null."),
                dispatch, token)));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider => new QueueScheduler(provider.GetRequiredService<IDatabaseQueue>(),
            provider.GetServices<ScheduleRegistration>(), provider.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>Opts into a native background poller. This schedules jobs; a queue worker executes them separately.</summary>
    public static IServiceCollection AddCaravelScheduler(this IServiceCollection services, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        if (interval < TimeSpan.FromMilliseconds(100) || interval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SchedulerHostedService>());
        services.AddSingleton(new SchedulerPolling(interval));
        return services;
    }

    private static void Name(string value, string argument)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 ||
            value.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_')))
            throw new ArgumentException("Use 1-64 lowercase ASCII letters, digits, dots, hyphens or underscores.", argument);
    }
}

internal sealed record ScheduleRegistration(string Name, long IntervalMilliseconds, string Queue, string TenantId,
    Func<IDatabaseQueue, QueueDispatchOptions, CancellationToken, Task<EnqueueResult>> Enqueue);
internal sealed record SchedulerPolling(TimeSpan Interval);

internal sealed class SchedulerHostedService(QueueScheduler scheduler, SchedulerPolling polling, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await scheduler.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(polling.Interval, clock, stoppingToken).ConfigureAwait(false);
        }
    }
}
