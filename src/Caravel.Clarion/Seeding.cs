using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Caravel.Clarion;

public interface IClarionSeeder
{
    Task SeedAsync(IClarion database, CancellationToken cancellationToken);
}

/// <summary>Explicit deterministic factory; builds POCOs and never saves implicitly.</summary>
public sealed class ModelFactory<T>(Func<int, T> create) where T : class
{
    public IReadOnlyList<T> Make(int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return Enumerable.Range(0, count).Select(create).ToArray();
    }

    public IReadOnlyList<T> AddTo(IClarion database, int count = 1)
    {
        var models = Make(count);
        database.Models<T>().AddRange(models);
        return models;
    }
}

public static class ClarionSeedingExtensions
{
    public static IServiceCollection AddClarionSeeder<T>(this IServiceCollection services) where T : class, IClarionSeeder
        => services.AddScoped<IClarionSeeder, T>();

    /// <summary>Runs seeders in registration order within one async scope. Seeders explicitly save their own work.</summary>
    public static async Task SeedClarionAsync(this IServiceProvider services, string? seeder = null, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var seeders = scope.ServiceProvider.GetServices<IClarionSeeder>().ToArray();
        if (seeder is not null)
        {
            seeders = seeders.Where(instance => instance.GetType().FullName == seeder || instance.GetType().Name == seeder).ToArray();
            if (seeders.Length != 1) throw new InvalidOperationException("The seeder name must identify exactly one registered seeder.");
        }
        if (seeders.Length == 0) throw new InvalidOperationException("No Clarion seeders are registered.");
        var database = scope.ServiceProvider.GetRequiredService<IClarion>();
        foreach (var instance in seeders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await instance.SeedAsync(database, cancellationToken);
        }
    }

    /// <summary>Explicit Bosun hook. Runs trusted application seeders without starting a web server.</summary>
    public static async Task<bool> RunCaravelSeedersAsync(this IHost host, string[] args, CancellationToken cancellationToken = default)
    {
        var command = Array.IndexOf(args, "--caravel-db-seed");
        if (command < 0) return false;
        if (command + 1 >= args.Length || args[command + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[command + 1]))
            throw new ArgumentException("--caravel-db-seed requires a new receipt file path.");
        var index = Array.IndexOf(args, "--seeder");
        if (index >= 0 && (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[index + 1])))
            throw new ArgumentException("--seeder requires a registered seeder name.");
        await using var receipt = new FileStream(args[command + 1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var seeder = index < 0 ? null : args[index + 1];
        await host.Services.SeedClarionAsync(seeder, cancellationToken);
        await JsonSerializer.SerializeAsync(receipt, new { completed = true, seeder }, cancellationToken: cancellationToken);
        return true;
    }
}
