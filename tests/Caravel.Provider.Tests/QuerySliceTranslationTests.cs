using System.Data.Common;
using Caravel.Clarion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Caravel.Provider.Tests;

public sealed class QuerySliceTranslationTests
{
    [Theory]
    [InlineData("sqlserver", false)]
    [InlineData("sqlserver", true)]
    [InlineData("postgres", false)]
    [InlineData("postgres", true)]
    public async Task Slice_query_shape_translates_without_opening_a_connection(string provider, bool includeTrashed)
    {
        var connections = new RejectConnections();
        var options = new DbContextOptionsBuilder<ActivityContext>();
        // Fixed synthetic configuration: never consult environment credentials or connect.
        if (provider == "sqlserver")
            options.UseSqlServer("Server=localhost;Database=TranslationOnly;Integrated Security=true;TrustServerCertificate=true");
        else
            options.UseNpgsql("Host=localhost;Database=TranslationOnly;Username=translation_only");
        options.AddInterceptors(connections);
        await using var db = new ActivityContext(options.Options);
        IQueryable<Activity> visible = db.Set<Activity>().AsNoTracking();
        if (includeTrashed) visible = visible.WithTrashed();
        var ordered = visible.Where(x => x.Quantity > 0)
            .OrderByDescending(x => x.Quantity).ThenBy(x => x.Id);

        // Translate the ordered Skip/Take shape used by ToSliceAsync. SQLite execution
        // tests separately verify the public API constructs and executes this shape.
        var sql = ordered.Skip(7).Take(3).ToQueryString();
        Assert.Contains("TenantId", sql);
        Assert.Contains("Quantity", sql);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COUNT(", sql, StringComparison.OrdinalIgnoreCase);
        var predicate = sql[(sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase))..];
        Assert.Contains("TenantId", predicate);
        if (includeTrashed)
            Assert.DoesNotContain("IS NULL", predicate, StringComparison.OrdinalIgnoreCase);
        else
        {
            Assert.Contains("DeletedAt", predicate);
            Assert.Contains("IS NULL", predicate, StringComparison.OrdinalIgnoreCase);
        }
        if (provider == "sqlserver")
        {
            Assert.Contains("OFFSET", sql);
            Assert.Contains("FETCH NEXT", sql);
            Assert.Contains(" = 7", sql);
            Assert.Contains(" = 3", sql); // Requested two rows plus one continuation peek.
        }
        else
        {
            Assert.Contains("LIMIT", sql);
            Assert.Contains("OFFSET", sql);
            Assert.Contains("='7'", sql);
            Assert.Contains("='3'", sql);
        }
        Assert.Equal(0, connections.Attempts);
    }

    private sealed class RejectConnections : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            Attempts++;
            throw new InvalidOperationException("Translation qualification must not open a connection.");
        }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("Translation qualification must not open a connection.");
        }
    }
}