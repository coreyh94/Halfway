using System.Collections.Concurrent;
using Halfway.Terminal.Windows;
using Xunit;

namespace Halfway.Terminal.Tests;

public sealed class ConPtyTerminalSessionTests
{
    [Fact]
    public async Task Cmd_round_trip_streams_output_and_exits_cleanly()
    {
        var marker = $"halfway-{Guid.NewGuid():N}";
        var output = new ConcurrentQueue<string>();
        var receivedOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedMarker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ConPtyTerminalSessionFactory();
        var options = new TerminalLaunchOptions(
            "cmd.exe",
            ["/q", "/k", $"echo {marker}"],
            Environment.CurrentDirectory,
            new TerminalSize(80, 24));

        await using var session = await factory.StartAsync(options);
        session.OutputReceived += (_, text) =>
        {
            output.Enqueue(text);
            receivedOutput.TrySetResult();
            if (string.Concat(output).Contains(marker, StringComparison.Ordinal))
            {
                receivedMarker.TrySetResult();
            }
        };

        await receivedOutput.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await receivedMarker.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.WriteAsync("exit\r");
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(marker, string.Concat(output), StringComparison.Ordinal);
        session.Resize(new TerminalSize(100, 32));
    }

    [Fact]
    public async Task Stop_cancels_an_interactive_process()
    {
        var factory = new ConPtyTerminalSessionFactory();
        var options = TerminalLaunchOptions.PowerShell(Path.GetTempPath());

        await using var session = await factory.StartAsync(options);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(session.Completion.IsCompleted);
    }

    [Fact]
    public async Task Two_sessions_remain_independent_when_one_is_stopped()
    {
        var factory = new ConPtyTerminalSessionFactory();
        var options = TerminalLaunchOptions.PowerShell(Environment.CurrentDirectory);

        await using var first = await factory.StartAsync(options);
        await using var second = await factory.StartAsync(options);
        await Task.Delay(250);

        Assert.NotEqual(first.ProcessId, second.ProcessId);
        Assert.False(first.Completion.IsCompleted);
        Assert.False(second.Completion.IsCompleted);

        await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);

        Assert.True(first.Completion.IsCompleted);
        Assert.False(second.Completion.IsCompleted);
    }
}
