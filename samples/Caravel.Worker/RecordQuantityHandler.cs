using Caravel.Queues;
using Microsoft.EntityFrameworkCore;

namespace Caravel.Worker;

public sealed record RecordQuantity(int Quantity);

public sealed class RecordQuantityHandler(ResultContext results) : IJobHandler<RecordQuantity>
{
    public async Task HandleAsync(RecordQuantity job, JobContext context, CancellationToken cancellationToken)
    {
        if (context.TenantId != SampleConfiguration.TenantId || context.Queue != SampleConfiguration.QueueName
            || job.Quantity is < 1 or > 3)
            throw new InvalidOperationException("This sample handles only its bounded synthetic quantity jobs.");
        var existing = await results.ProcessedQuantities.AsNoTracking()
            .SingleOrDefaultAsync(x => x.JobId == context.JobId, cancellationToken);
        if (existing is not null)
        {
            VerifyDuplicate(existing, job, context);
            return;
        }
        results.ProcessedQuantities.Add(new ProcessedQuantity
        {
            JobId = context.JobId, TenantId = context.TenantId, Quantity = job.Quantity
        });
        try
        {
            // A single insert atomically records the job ID and its contribution to the derived aggregate.
            await results.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another attempt may have committed first while this worker's queue lease expired.
            existing = await results.ProcessedQuantities.AsNoTracking()
                .SingleOrDefaultAsync(x => x.JobId == context.JobId, cancellationToken);
            if (existing is null) throw;
            VerifyDuplicate(existing, job, context);
            return;
        }

        // Explicit fault injection for this synthetic sample only: exit after the result commits, before queue acknowledgement.
        if (Environment.GetEnvironmentVariable("CARAVEL_WORKER_CRASH_AFTER_RESULT") == "1")
            Environment.Exit(73);
    }

    private static void VerifyDuplicate(ProcessedQuantity existing, RecordQuantity requested, JobContext context)
    {
        if (existing.TenantId != context.TenantId || existing.Quantity != requested.Quantity)
            throw new InvalidOperationException("The processed job ID is associated with a different synthetic result.");
    }
}
