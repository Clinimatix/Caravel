using Caravel.Queues;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Caravel.IdentitySample;

/// <summary>Reads required tables and columns without creating or changing the configured database.</summary>
public sealed class BackendReadinessCheck(string connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var readOnly = new SqliteConnectionStringBuilder(connection)
        { Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString();
        await using var app = new IdentityContext(new DbContextOptionsBuilder<IdentityContext>().UseSqlite(readOnly).Options);
        // Materialize at most one full row so missing columns fail even when the table is empty.
        await app.Users.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await app.Roles.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await app.UserRoles.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await app.UserClaims.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await app.RoleClaims.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await app.UserLogins.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await app.UserTokens.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await app.Notes.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await app.CounterResults.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        await using var queue = new QueueDbContext(new DbContextOptionsBuilder<QueueDbContext>().UseSqlite(readOnly).Options);
        await queue.Jobs.AsNoTracking().Take(1).ToListAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
