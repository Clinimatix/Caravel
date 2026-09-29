using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caravel.Clarion;

public interface IClarion
{
    DbContext Context { get; }
    DbSet<T> Models<T>() where T : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<T?> RestoreAsync<T>(object?[] keyValues, CancellationToken cancellationToken = default) where T : class, ISoftDeletable;
}

internal sealed class ClarionSession(DbContext context) : IClarion
{
    public DbContext Context => context;
    public DbSet<T> Models<T>() where T : class => context.Set<T>();
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);

    /// <summary>Stages restoration without saving; keeps every query filter except ClarionSoftDelete enabled.</summary>
    public async Task<T?> RestoreAsync<T>(object?[] keyValues, CancellationToken cancellationToken = default) where T : class, ISoftDeletable
    {
        ArgumentNullException.ThrowIfNull(keyValues);
        var entity = context.Model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped.");
        var key = entity.FindPrimaryKey() ?? throw new InvalidOperationException("Cannot restore an entity without a primary key.");
        if (key.Properties.Count != keyValues.Length) throw new ArgumentException("Key values must match the primary key property count and order.", nameof(keyValues));
        var parameter = Expression.Parameter(typeof(T), "model");
        Expression? predicate = null;
        for (var index = 0; index < keyValues.Length; index++)
        {
            var property = key.Properties[index];
            if (keyValues[index] is null || !property.ClrType.IsInstanceOfType(keyValues[index]))
                throw new ArgumentException($"Key '{property.Name}' requires a non-null {property.ClrType.Name}.", nameof(keyValues));
            var equal = Expression.Equal(
                Expression.Call(typeof(EF), nameof(EF.Property), [property.ClrType], parameter, Expression.Constant(property.Name)),
                Expression.Constant(keyValues[index], property.ClrType));
            predicate = predicate is null ? equal : Expression.AndAlso(predicate, equal);
        }
        var model = await context.Set<T>().WithTrashed().AsTracking()
            .SingleOrDefaultAsync(Expression.Lambda<Func<T, bool>>(predicate!, parameter), cancellationToken);
        if (model?.DeletedAt is not null)
        {
            model.DeletedAt = null;
            context.Entry(model).Property(nameof(ISoftDeletable.DeletedAt)).IsModified = true;
        }
        return model;
    }
}

public static class ClarionExtensions
{
    public static IServiceCollection AddClarion<TContext>(this IServiceCollection services, Action<DbContextOptionsBuilder> configure)
        where TContext : DbContext => services.AddClarion<TContext>((_, options) => configure(options));

    public static IServiceCollection AddClarion<TContext>(this IServiceCollection services, Action<IServiceProvider, DbContextOptionsBuilder> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(service => service.ServiceType == typeof(IClarion)))
            throw new InvalidOperationException("A scoped Clarion session is already registered. Use native DbContext registrations for additional databases.");
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ClarionSaveChangesInterceptor>();
        services.AddDbContext<TContext>((provider, options) =>
        {
            configure(provider, options);
            options.AddInterceptors(provider.GetRequiredService<ClarionSaveChangesInterceptor>());
        });
        services.AddScoped<IClarion>(provider => new ClarionSession(provider.GetRequiredService<TContext>()));
        return services;
    }

    public static async Task<IReadOnlyList<T>> GetAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default)
        => await query.ToListAsync(cancellationToken);

    /// <summary>Disables only the named soft-delete filter, preserving tenant and other application filters.</summary>
    public static IQueryable<T> WithTrashed<T>(this IQueryable<T> query) where T : class, ISoftDeletable
        => query.IgnoreQueryFilters([ClarionModelExtensions.SoftDeleteFilter]);
}
