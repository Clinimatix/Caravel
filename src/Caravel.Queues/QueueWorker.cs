using System.Text;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Caravel.Queues;

public sealed class QueueWorker
{
    private readonly IDatabaseQueue queue;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly JobRegistry registry;
    private readonly DatabaseQueueOptions options;
    private readonly TimeProvider clock;

    internal QueueWorker(IDatabaseQueue queue, IServiceScopeFactory scopeFactory, JobRegistry registry,
        DatabaseQueueOptions options, TimeProvider clock)
        => (this.queue, this.scopeFactory, this.registry, this.options, this.clock) = (queue, scopeFactory, registry, options, clock);

    /// <summary>Process at most one job. Exceptions in a handler become bounded retries; infrastructure errors propagate.</summary>
    public async Task<bool> RunOnceAsync(string queueName, CancellationToken cancellationToken = default)
    {
        var lease = await queue.TryClaimAsync(queueName, cancellationToken).ConfigureAwait(false);
        if (lease is null) return false;
        var registration = registry.Find(lease.JobType);
        using var activity = QueueDiagnostics.Start(lease, registration?.Name);
        try
        {
            if (registration is null)
            {
                await RecordFailureAsync(lease, QueueFailure.UnknownJobType, activity, cancellationToken).ConfigureAwait(false);
                return true;
            }
            if (Encoding.UTF8.GetByteCount(lease.Payload) > options.MaxPayloadBytes)
            {
                await RecordFailureAsync(lease, QueueFailure.InvalidPayload, activity, cancellationToken).ConfigureAwait(false);
                return true;
            }
            var remaining = lease.ExpiresAt - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                QueueDiagnostics.Finish(activity, "lease_lost", "LeaseExpired");
                return true;
            }
            using var deadline = new CancellationTokenSource(remaining, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var leaseLost = false;
            var renewal = options.LeaseRenewalInterval is { } interval
                ? RenewUntilStoppedAsync(interval) : Task.CompletedTask;
            async Task RenewUntilStoppedAsync(TimeSpan interval)
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(interval, clock, renewalStop.Token).ConfigureAwait(false);
                        var renewed = await queue.RenewAsync(lease, renewalStop.Token).ConfigureAwait(false);
                        if (renewed is null || deadline.IsCancellationRequested)
                        {
                            leaseLost = true;
                            await deadline.CancelAsync().ConfigureAwait(false);
                            return;
                        }
                        lease = renewed;
                        var remainingLease = renewed.ExpiresAt - clock.GetUtcNow();
                        if (remainingLease <= TimeSpan.Zero)
                        {
                            leaseLost = true;
                            await deadline.CancelAsync().ConfigureAwait(false);
                            return;
                        }
                        deadline.CancelAfter(remainingLease);
                    }
                }
                catch (OperationCanceledException) when (renewalStop.IsCancellationRequested) { }
                catch
                {
                    leaseLost = true;
                    await deadline.CancelAsync().ConfigureAwait(false);
                    throw;
                }
            }
            QueueFailure? failure = null;
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                await using var scope = scopeFactory.CreateAsyncScope();
                failure = await registration.InvokeAsync(scope.ServiceProvider, lease.Payload, lease.Context, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown leaves the lease for expiry/recovery. Previously performed side effects may run again.
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { failure = QueueFailure.LeaseExpired; }
            catch (Exception) { failure = QueueFailure.HandlerFailed; }
            finally
            {
                await renewalStop.CancelAsync().ConfigureAwait(false);
                await renewal.ConfigureAwait(false);
            }

            if (leaseLost)
            {
                QueueDiagnostics.Finish(activity, "lease_lost", "LeaseLost");
                return true;
            }

            if (failure is { } reason)
                await RecordFailureAsync(lease, reason, activity, cancellationToken).ConfigureAwait(false);
            else
            {
                var completed = await queue.CompleteAsync(lease, cancellationToken).ConfigureAwait(false);
                QueueDiagnostics.Finish(activity, completed ? "completed" : "lease_lost", completed ? null : "LeaseLost");
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            QueueDiagnostics.Finish(activity, "cancelled", "Cancelled");
            throw;
        }
        catch (Exception)
        {
            QueueDiagnostics.Finish(activity, "infrastructure_error", "InfrastructureError");
            throw;
        }
    }

    private async Task RecordFailureAsync(QueueLease lease, QueueFailure failure, Activity? activity, CancellationToken cancellationToken)
    {
        var recorded = await queue.FailAsync(lease, failure, cancellationToken).ConfigureAwait(false);
        QueueDiagnostics.Finish(activity, recorded ? "failed" : "lease_lost", recorded ? failure.ToString() : "LeaseLost");
    }
}

internal sealed class QueueHostedService(QueueWorker worker, string queue, TimeSpan interval, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await worker.RunOnceAsync(queue, stoppingToken).ConfigureAwait(false))
                await Task.Delay(interval, clock, stoppingToken).ConfigureAwait(false);
        }
    }
}
