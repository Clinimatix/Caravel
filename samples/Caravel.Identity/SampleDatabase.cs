using Microsoft.EntityFrameworkCore;

namespace Caravel.IdentitySample;

/// <summary>Provider selection belongs to the application; migrations must match that provider.</summary>
public static class SampleDatabase
{
    public static void Configure(DbContextOptionsBuilder options, string provider, string connection, bool queue = false)
    {
        var assembly = typeof(Program).Assembly.GetName().Name;
        switch (provider)
        {
            case "sqlite":
                options.UseSqlite(connection, sql =>
                {
                    sql.MigrationsAssembly(assembly);
                    if (queue) sql.MigrationsHistoryTable("__CaravelQueueMigrations");
                });
                break;
            case "sqlserver":
                options.UseSqlServer(connection, sql =>
                {
                    sql.MigrationsAssembly(assembly);
                    if (queue) sql.MigrationsHistoryTable("__CaravelQueueMigrations");
                });
                break;
            case "postgres":
                options.UseNpgsql(connection, sql =>
                {
                    sql.MigrationsAssembly(assembly);
                    if (queue) sql.MigrationsHistoryTable("__CaravelQueueMigrations");
                });
                break;
            default:
                throw new InvalidOperationException("Caravel:DatabaseProvider must be sqlite, sqlserver or postgres.");
        }
    }
}
