using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Caravel.Queues;

internal static class QueueDiagnostics
{
    private const string Name = "Caravel.Queues";
    private static readonly Meter Meter = new(Name);
    private static readonly ActivitySource Activities = new(Name);
    internal static readonly Counter<long> Accepted = Counter("accepted", "New jobs durably accepted.");
    internal static readonly Counter<long> Deduplicated = Counter("deduplicated", "Matching enqueue requests accepted as existing jobs.");
    internal static readonly Counter<long> Claimed = Counter("claimed", "Job leases successfully acquired.");
    internal static readonly Counter<long> Completed = Counter("completed", "Jobs durably marked complete by a current lease.");
    internal static readonly Counter<long> Failed = Counter("failed", "Failures recorded by a current lease.");
    internal static readonly Counter<long> Retried = Counter("retried", "Jobs durably scheduled for another attempt after failure.");
    internal static readonly Counter<long> DeadLettered = Counter("deadlettered", "Jobs durably moved to dead letter.");
    internal static readonly Counter<long> Replayed = Counter("replayed", "Dead-letter jobs durably scheduled for explicit replay.");
    internal static readonly Counter<long> LeaseLost = Counter("lease_lost", "Completion or failure acknowledgements rejected for a noncurrent or expired lease.");

    internal static void Add(Counter<long> counter, string queue, long count = 1, QueueFailure? failure = null)
    {
        if (count == 0) return;
        TagList tags = new() { { "queue", queue } };
        if (failure.HasValue) tags.Add("failure", failure.Value.ToString());
        counter.Add(count, tags);
    }

    internal static Activity? Start(QueueLease lease, string? registeredJobType)
    {
        var activity = Activities.StartActivity("caravel.queue.process", ActivityKind.Consumer);
        activity?.SetTag("messaging.destination.name", lease.Context.Queue);
        activity?.SetTag("messaging.message.id", lease.Context.JobId.ToString());
        if (registeredJobType is not null) activity?.SetTag("caravel.job.type", registeredJobType);
        return activity;
    }

    internal static void Finish(Activity? activity, string outcome, string? failure = null)
    {
        activity?.SetTag("caravel.queue.outcome", outcome);
        if (failure is not null) activity?.SetTag("caravel.queue.failure", failure);
        activity?.SetStatus(outcome == "completed" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
    }

    private static Counter<long> Counter(string suffix, string description) =>
        Meter.CreateCounter<long>("caravel.queue.jobs." + suffix, unit: "{job}", description: description);
}
