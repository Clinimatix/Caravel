using Caravel.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Caravel.Worker;

public sealed class QueueDesignTimeFactory : IDesignTimeDbContextFactory<QueueDbContext>
{
    public QueueDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<QueueDbContext>();
        SampleConfiguration.Load().ConfigureQueue(options);
        return new(options.Options);
    }
}

public sealed class ResultDesignTimeFactory : IDesignTimeDbContextFactory<ResultContext>
{
    public ResultContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ResultContext>();
        SampleConfiguration.Load().ConfigureResults(options);
        return new(options.Options);
    }
}
