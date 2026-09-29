using Caravel.Clarion;
using Microsoft.EntityFrameworkCore;

namespace Caravel.Data;

public sealed class ActivityContext(DbContextOptions<ActivityContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.AddClarionModels(typeof(ActivityContext).Assembly);
        model.Entity<Activity>().HasIndex(x => x.EventKey).IsUnique();
        model.Entity<Activity>().Property(x => x.EventKey).HasMaxLength(80);
        model.Entity<Activity>().Property(x => x.Kind).HasMaxLength(40);
        model.ApplyClarionConventions();
    }
}

[ClarionModel]
public sealed class Activity : ITimestamped, ISoftDeletable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Quantity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class SyntheticActivitySeeder : IClarionSeeder
{
    public async Task SeedAsync(IClarion db, CancellationToken cancellationToken)
    {
        // Explicit synthetic seed, repeatable by key; never called on normal startup.
        var factory = new ModelFactory<Activity>(index => new Activity
        {
            EventKey = $"synthetic-{index}", Kind = "processed", Quantity = index + 1
        });
        foreach (var activity in factory.Make(3))
            if (!await db.Models<Activity>().WithTrashed().AnyAsync(x => x.EventKey == activity.EventKey, cancellationToken))
                db.Models<Activity>().Add(activity);
        await db.SaveChangesAsync(cancellationToken);
    }
}
