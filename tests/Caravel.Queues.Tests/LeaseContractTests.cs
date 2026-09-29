using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Queues.Tests;

public sealed partial class DatabaseQueueTests
{
    [Fact]
    public async Task RenewalIsBoundedByOriginalClaimAndCannotReviveExpiredOrReclaimedTokens()
    {
        await using var store = await TestStore.Create(options => options.MaxLeaseLifetime = TimeSpan.FromMinutes(2));
        await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "renew"));
        var first = Assert.IsType<QueueLease>(await store.Queue.TryClaimAsync("reports"));
        store.Clock.Advance(TimeSpan.FromSeconds(45));
        var renewed = Assert.IsType<QueueLease>(await store.Queue.RenewAsync(first));
        Assert.Equal(first.ExpiresAt.AddSeconds(45), renewed.ExpiresAt);
        store.Clock.Advance(TimeSpan.FromSeconds(45));
        renewed = Assert.IsType<QueueLease>(await store.Queue.RenewAsync(renewed));
        Assert.Equal(first.ExpiresAt.AddMinutes(1), renewed.ExpiresAt);
        store.Clock.Advance(TimeSpan.FromSeconds(29));
        renewed = Assert.IsType<QueueLease>(await store.Queue.RenewAsync(renewed));
        Assert.Equal(first.ExpiresAt.AddMinutes(1), renewed.ExpiresAt);
        Assert.Null(await store.Queue.TryClaimAsync("reports"));
        store.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await store.Queue.RenewAsync(renewed));
        Assert.False(await store.Queue.CompleteAsync(first));
        var next = Assert.IsType<QueueLease>(await store.Queue.TryClaimAsync("reports"));
        Assert.Null(await store.Queue.RenewAsync(first));
        Assert.Null(await store.Queue.RenewAsync(renewed));
        Assert.False(await store.Queue.FailAsync(first, QueueFailure.HandlerFailed));
        Assert.True(await store.Queue.CompleteAsync(next));
        Assert.Null(await store.Queue.RenewAsync(next));
    }

    [Fact]
    public void RenewalRejectsIntervalsThatCannotSafelyPrecedeExpiryAndUnboundedLifetimes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddCaravelDatabaseQueue(
            options => options.LeaseRenewalInterval = TimeSpan.FromMilliseconds(99)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddCaravelDatabaseQueue(
            options => options.LeaseRenewalInterval = options.LeaseDuration));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddCaravelDatabaseQueue(
            options => options.MaxLeaseLifetime = TimeSpan.FromDays(8)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddCaravelDatabaseQueue(
            options => options.MaxLeaseLifetime = TimeSpan.FromSeconds(1)));
    }
}
