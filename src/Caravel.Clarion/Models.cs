using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Caravel.Clarion;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ClarionModelAttribute : Attribute;

public interface ITimestamped
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}

public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; set; }
}

public static class ClarionModelExtensions
{
    public const string SoftDeleteFilter = "ClarionSoftDelete";

    /// <summary>Discovers only attributed models in explicitly supplied assemblies.</summary>
    public static ModelBuilder AddClarionModels(this ModelBuilder builder, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var type in assemblies.Distinct().SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsDefined(typeof(ClarionModelAttribute), inherit: false))
            .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
                throw new InvalidOperationException($"Clarion model {type.FullName} must be a concrete, closed class.");
            builder.Entity(type);
        }
        return builder;
    }

    /// <summary>Call after model discovery/configuration. Uses EF Core 10's named query filters.</summary>
    public static ModelBuilder ApplyClarionConventions(this ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes().ToArray())
        {
            if (!typeof(ISoftDeletable).IsAssignableFrom(entity.ClrType)) continue;
            if (entity.IsOwned() || entity.FindPrimaryKey() is null)
                throw new InvalidOperationException($"Soft deletion requires a non-owned keyed entity: {entity.ClrType.Name}.");
            if (entity.BaseType is not null)
            {
                if (!typeof(ISoftDeletable).IsAssignableFrom(entity.GetRootType().ClrType))
                    throw new InvalidOperationException("Soft-delete inheritance requires the root entity to implement ISoftDeletable.");
                continue;
            }
            var parameter = Expression.Parameter(entity.ClrType, "model");
            var deletedAt = Expression.Call(typeof(EF), nameof(EF.Property), [typeof(DateTimeOffset?)], parameter, Expression.Constant(nameof(ISoftDeletable.DeletedAt)));
            builder.Entity(entity.ClrType).HasQueryFilter(SoftDeleteFilter,
                Expression.Lambda(Expression.Equal(deletedAt, Expression.Constant(null, typeof(DateTimeOffset?))), parameter));
        }
        return builder;
    }
}
