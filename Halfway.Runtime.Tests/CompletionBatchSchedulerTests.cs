using Halfway.Runtime;
using Xunit;

namespace Halfway.Runtime.Tests;

public sealed class CompletionBatchSchedulerTests
{
    [Fact]
    public async Task WindowIsAnchoredToFirstCompletionAndBoundaryStartsNextBatch()
    {
        var start = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var timer = new ManualBatchTimer(start);
        var due = new List<DateTimeOffset>();
        var firstDue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentId = Guid.NewGuid();
        using var scheduler = new CompletionBatchScheduler(timer, (_, _, deadline) =>
        {
            lock (due) due.Add(deadline);
            firstDue.TrySetResult();
            return Task.CompletedTask;
        });

        var firstDeadline = scheduler.Schedule(parentId);
        timer.SetNow(start.AddMilliseconds(249));
        Assert.Equal(firstDeadline, scheduler.Schedule(parentId));
        timer.SetNow(firstDeadline);
        var secondDeadline = scheduler.Schedule(parentId);

        Assert.Equal(start.AddMilliseconds(250), firstDeadline);
        Assert.Equal(start.AddMilliseconds(500), secondDeadline);
        timer.ReleaseDueDelays();
        await firstDue.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(firstDeadline, due);
    }

    [Fact]
    public async Task SteadyStreamCannotExtendFirstDeadline()
    {
        var start = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var timer = new ManualBatchTimer(start);
        var fired = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentId = Guid.NewGuid();
        using var scheduler = new CompletionBatchScheduler(timer, (_, _, deadline) =>
        {
            fired.TrySetResult(deadline);
            return Task.CompletedTask;
        });

        var anchored = scheduler.Schedule(parentId);
        foreach (var offset in new[] { 40, 80, 120, 160, 200, 249 })
        {
            timer.SetNow(start.AddMilliseconds(offset));
            Assert.Equal(anchored, scheduler.Schedule(parentId));
        }

        timer.SetNow(anchored);
        timer.ReleaseDueDelays();
        Assert.Equal(anchored, await fired.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class ManualBatchTimer(DateTimeOffset now) : ICompletionBatchTimer
    {
        private readonly object _gate = new();
        private readonly List<(DateTimeOffset Deadline, TaskCompletionSource Completion)> _delays = [];
        public DateTimeOffset UtcNow { get; private set; } = now;

        public Task DelayUntilAsync(DateTimeOffset deadlineUtc, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (deadlineUtc <= UtcNow) return Task.CompletedTask;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                _delays.Add((deadlineUtc, completion));
                return completion.Task;
            }
        }

        public void SetNow(DateTimeOffset value)
        {
            lock (_gate) UtcNow = value;
        }

        public void ReleaseDueDelays()
        {
            TaskCompletionSource[] due;
            lock (_gate)
            {
                due = _delays.Where(item => item.Deadline <= UtcNow).Select(item => item.Completion).ToArray();
                _delays.RemoveAll(item => item.Deadline <= UtcNow);
            }
            foreach (var completion in due) completion.TrySetResult();
        }
    }
}
