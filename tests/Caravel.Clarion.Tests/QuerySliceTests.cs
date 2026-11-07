using System.Data.Common;
using Caravel.Clarion;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Caravel.Clarion.Tests;

public sealed class QuerySliceTests
{
    [Fact]
    public async Task PagesVisibleRowsWithTiesWithoutCounting()
    {
        await using var fixture = await Fixture.Create();
        var query = fixture.Db.Rows.AsNoTracking().OrderBy(row => row.Sort).ThenBy(row => row.Id);
        var first = await query.ToSliceAsync(pageSize: 2);
        Assert.Equal(new[] { 1, 3 }, first.Items.Select(row => row.Id));
        Assert.True(first.HasMore);
        Assert.Equal(2, first.NextOffset);
        Assert.Equal(0, first.Offset);
        Assert.Equal(2, first.PageSize);
        Assert.DoesNotContain("COUNT(", Assert.Single(fixture.Commands.Text), StringComparison.OrdinalIgnoreCase);
        fixture.Commands.Text.Clear();
        var last = await query.ToSliceAsync(offset: first.NextOffset!.Value, pageSize: 2);
        Assert.Equal(new[] { 4, 6 }, last.Items.Select(row => row.Id));
        Assert.False(last.HasMore);
        Assert.Null(last.NextOffset);
        Assert.Single(fixture.Commands.Text);
    }

    [Fact]
    public async Task HiddenAndDeletedRowsDoNotCreatePhantomContinuation()
    {
        await using var fixture = await Fixture.Create();
        var result = await fixture.Db.Rows.OrderBy(row => row.Id).ToSliceAsync(pageSize: 4);
        Assert.Equal(new[] { 1, 3, 4, 6 }, result.Items.Select(row => row.Id));
        Assert.False(result.HasMore);
        Assert.Null(result.NextOffset);
        Assert.Single(fixture.Commands.Text);
    }

    [Fact]
    public async Task PreservesProjectionFilteringAndDescendingOrder()
    {
        await using var fixture = await Fixture.Create();
        var result = await fixture.Db.Rows.Where(row => row.Id > 1)
            .Select(row => new { row.Id, row.Sort }).OrderByDescending(row => row.Sort)
            .ThenByDescending(row => row.Id).ToSliceAsync(pageSize: 2);
        Assert.Equal(new[] { 6, 4 }, result.Items.Select(row => row.Id));
        Assert.True(result.HasMore);
        Assert.Single(fixture.Commands.Text);
    }

    [Fact]
    public async Task PastEndAndEmptyQueryHaveNoContinuation()
    {
        await using var fixture = await Fixture.Create();
        var pastEnd = await fixture.Db.Rows.OrderBy(row => row.Id).ToSliceAsync(offset: 20);
        Assert.Empty(pastEnd.Items);
        Assert.False(pastEnd.HasMore);
        Assert.Null(pastEnd.NextOffset);
        var empty = await fixture.Db.Rows.Where(row => row.Id < 0).OrderBy(row => row.Id).ToSliceAsync();
        Assert.Empty(empty.Items);
        Assert.False(empty.HasMore);
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 201)]
    [InlineData(0, int.MaxValue)]
    [InlineData(int.MaxValue, 2)]
    public async Task InvalidBoundsFailBeforeQuery(int offset, int pageSize)
    {
        await using var fixture = await Fixture.Create();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Db.Rows
            .OrderBy(row => row.Id).ToSliceAsync(offset, pageSize));
        Assert.Empty(fixture.Commands.Text);
    }

    [Fact]
    public async Task CancellationIsPropagatedBeforeQuery()
    {
        await using var fixture = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Db.Rows
            .OrderBy(row => row.Id).ToSliceAsync(cancellationToken: cancellation.Token));
        Assert.Empty(fixture.Commands.Text);
    }

    [Fact]
    public async Task NullQueryIsRejected()
    {
        IOrderedQueryable<Row> query = null!;
        await Assert.ThrowsAsync<ArgumentNullException>(() => query.ToSliceAsync());
    }

    public sealed class Row
    {
        public int Id { get; set; }
        public int Sort { get; set; }
        public string Account { get; set; } = "client";
        public bool Deleted { get; set; }
    }

    private sealed class Database(DbContextOptions<Database> options) : DbContext(options)
    {
        public DbSet<Row> Rows => Set<Row>();
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Row>().HasQueryFilter(row => row.Account == "client" && !row.Deleted);
    }

    private sealed class Commands : DbCommandInterceptor
    {
        public List<string> Text { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Text.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Fixture(SqliteConnection connection, Database db, Commands commands) : IAsyncDisposable
    {
        public Database Db => db;
        public Commands Commands => commands;
        public static async Task<Fixture> Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var commands = new Commands();
            var db = new Database(new DbContextOptionsBuilder<Database>().UseSqlite(connection)
                .AddInterceptors(commands).Options);
            await db.Database.EnsureCreatedAsync();
            db.Rows.AddRange(new Row { Id = 1, Sort = 10 }, new Row { Id = 2, Sort = 10, Account = "hidden" },
                new Row { Id = 3, Sort = 10 }, new Row { Id = 4, Sort = 20 },
                new Row { Id = 5, Sort = 20, Deleted = true }, new Row { Id = 6, Sort = 20 });
            await db.SaveChangesAsync();
            commands.Text.Clear();
            return new Fixture(connection, db, commands);
        }
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
