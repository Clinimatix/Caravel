using Microsoft.EntityFrameworkCore;

namespace Caravel.Worker;

public sealed class ResultContext(DbContextOptions<ResultContext> options) : DbContext(options)
{
    public DbSet<ProcessedQuantity> ProcessedQuantities => Set<ProcessedQuantity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var result = modelBuilder.Entity<ProcessedQuantity>();
        result.HasKey(x => x.JobId);
        result.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        result.HasIndex(x => x.TenantId);
    }
}

public sealed class ProcessedQuantity
{
    public Guid JobId { get; set; }
    public string TenantId { get; set; } = "";
    public int Quantity { get; set; }
}
