using Halfway.Runtime;
using Xunit;

namespace Halfway.Runtime.Tests;

public sealed class SubmittedInputQueueTests
{
    [Fact]
    public async Task AcceptanceIsAcknowledgedBeforeTerminalWriteCompletion()
    {
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new SubmittedInputQueue(async (_, token) =>
        {
            writeStarted.TrySetResult();
            await releaseWrite.Task.WaitAsync(token);
        });

        var acceptance = queue.Accept("accepted");
        await writeStarted.Task;

        Assert.False(acceptance.Completion.IsCompleted);
        Assert.Equal(1, queue.Count);
        releaseWrite.TrySetResult();
        await acceptance.Completion;
    }

    [Fact]
    public async Task AcceptedEntriesAreDeliveredInFifoOrder()
    {
        var delivered = new List<string>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new SubmittedInputQueue(async (input, token) =>
        {
            delivered.Add(input);
            if (input == "first")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
        });

        var first = queue.EnqueueAsync("first");
        await firstStarted.Task;
        var second = queue.EnqueueAsync("second");
        var third = queue.EnqueueAsync("third");
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(["first", "second", "third"], delivered);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task CapacityRejectsNewestWithoutDroppingAcceptedEntries()
    {
        var delivered = new List<string>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new SubmittedInputQueue(async (input, token) =>
        {
            delivered.Add(input);
            if (input == "first")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
        }, capacity: 2);

        var first = queue.EnqueueAsync("first"); await firstStarted.Task;
        var second = queue.EnqueueAsync("second");
        var rejected = await Assert.ThrowsAsync<SubmittedInputQueueFullException>(() => queue.EnqueueAsync("newest"));
        Assert.Contains("capacity 2", rejected.Message, StringComparison.Ordinal);
        releaseFirst.TrySetResult(); await Task.WhenAll(first, second);

        Assert.Equal(["first", "second"], delivered);
    }

    [Fact]
    public async Task CancellationAndCloseResolveEntriesWithoutDeliveryOrReplay()
    {
        var delivered = new List<string>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new SubmittedInputQueue(async (input, token) =>
        {
            delivered.Add(input);
            firstStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        using var cancellation = new CancellationTokenSource();

        var first = queue.EnqueueAsync("first"); await firstStarted.Task;
        var cancelled = queue.EnqueueAsync("cancelled", cancellation.Token); cancellation.Cancel();
        var queued = queue.EnqueueAsync("queued");
        var reason = new SessionInputUnavailableException("runtime"); queue.Close(reason);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Same(reason, await Assert.ThrowsAsync<SessionInputUnavailableException>(() => first));
        Assert.Same(reason, await Assert.ThrowsAsync<SessionInputUnavailableException>(() => queued));
        Assert.Equal(["first"], delivered);
        Assert.Equal(0, queue.Count);
    }
}
