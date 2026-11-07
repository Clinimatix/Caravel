using Microsoft.EntityFrameworkCore;

namespace Caravel.Clarion;

/// <summary>A bounded query result without a total count.</summary>
public sealed record QuerySlice<T>(IReadOnlyList<T> Items, int Offset, int PageSize, bool HasMore)
{
    public int? NextOffset => HasMore ? checked(Offset + Items.Count) : null;
}

public static class QuerySliceExtensions
{
    /// <summary>
    /// Reads a slice of an already filtered, explicitly ordered query with one extra row
    /// for continuation. Supply a unique tie breaker; concurrent writes can shift offsets.
    /// </summary>
    public static async Task<QuerySlice<T>> ToSliceAsync<T>(this IOrderedQueryable<T> query,
        int offset = 0, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (pageSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Use a page size from 1 through 200.");
        if (offset < 0 || offset > int.MaxValue - pageSize)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset plus page size must fit a nonnegative Int32.");
        cancellationToken.ThrowIfCancellationRequested();
        var items = await query.Skip(offset).Take(pageSize + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(pageSize);
        return new(items.AsReadOnly(), offset, pageSize, hasMore);
    }
}
