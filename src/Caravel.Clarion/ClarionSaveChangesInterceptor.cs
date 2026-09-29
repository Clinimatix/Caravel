using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Caravel.Clarion;

/// <summary>Opt-in persistence conventions. Direct ExecuteUpdate/Delete and raw SQL retain native EF semantics.</summary>
public sealed class ClarionSaveChangesInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Prepare(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Prepare(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Prepare(DbContext? context)
    {
        if (context is null) return;
        context.ChangeTracker.DetectChanges();
        var entries = context.ChangeTracker.Entries().ToArray();
        var softDeletes = entries.Where(entry => entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable).ToArray();
        // A native cascade may already have marked children Deleted. Never silently turn a parent deletion into child data loss.
        if (softDeletes.Length > 0 && entries.Any(entry => entry.State == EntityState.Deleted && entry.Entity is not ISoftDeletable))
            throw new InvalidOperationException("Soft deletion cannot be saved with hard deletions (including cascade-deleted dependents). Restore tracked states and configure explicit soft-delete relationships or save independent hard deletions separately.");
        foreach (var principal in softDeletes)
        foreach (var dependent in entries.Where(entry => entry.State == EntityState.Modified))
        foreach (var relationship in dependent.Metadata.GetForeignKeys())
        {
            if (!relationship.PrincipalEntityType.IsAssignableFrom(principal.Metadata)
                || !relationship.Properties.Any(property => dependent.Property(property.Name).IsModified)) continue;
            var referencedParent = relationship.Properties.Select((property, index) => Equals(
                dependent.Property(property.Name).OriginalValue,
                principal.Property(relationship.PrincipalKey.Properties[index].Name).OriginalValue)).All(matches => matches);
            if (referencedParent)
                throw new InvalidOperationException("Soft deletion cannot be saved with dependent relationship changes (including ClientSetNull cascade fixups). Reload tracked states and configure explicit relationship behavior before retrying.");
        }
        var now = clock.GetUtcNow();
        foreach (var entry in softDeletes)
        {
            var properties = ScalarProperties(entry.Properties, entry.ComplexProperties).ToArray();
            // Keep explicit application-managed token changes so a soft delete invalidates stale writers.
            // Clearing IsModified also restores the original value, so select these before changing state.
            var changedTokens = properties.Where(property => property.Metadata.IsConcurrencyToken
                && (property.Metadata.ValueGenerated & ValueGenerated.OnUpdate) == 0
                && property.Metadata.GetAfterSaveBehavior() == PropertySaveBehavior.Save
                && !property.Metadata.GetValueComparer().Equals(property.CurrentValue, property.OriginalValue))
                .Select(property => property.Metadata).ToHashSet();
            entry.State = EntityState.Modified;
            foreach (var property in properties) property.IsModified = changedTokens.Contains(property.Metadata);
            ((ISoftDeletable)entry.Entity).DeletedAt = now;
            entry.Property(nameof(ISoftDeletable.DeletedAt)).IsModified = true;
        }
        foreach (var entry in entries)
        {
            if (entry.Entity is not ITimestamped model) continue;
            if (entry.State == EntityState.Added)
            {
                if (model.CreatedAt == default) model.CreatedAt = now;
                model.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                var created = entry.Property(nameof(ITimestamped.CreatedAt));
                created.CurrentValue = created.OriginalValue;
                created.IsModified = false;
                model.UpdatedAt = now;
                entry.Property(nameof(ITimestamped.UpdatedAt)).IsModified = true;
            }
        }
    }

    private static IEnumerable<PropertyEntry> ScalarProperties(IEnumerable<PropertyEntry> properties,
        IEnumerable<ComplexPropertyEntry> complexProperties)
        => properties.Concat(complexProperties.SelectMany(complex => ScalarProperties(complex.Properties, complex.ComplexProperties)));
}
