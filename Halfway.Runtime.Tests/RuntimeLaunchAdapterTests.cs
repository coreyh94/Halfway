using Halfway.Terminal;
using Xunit;

namespace Halfway.Runtime.Tests;

public sealed class RuntimeLaunchAdapterTests
{
    [Fact]
    public void PowerShell_is_the_deterministic_default_launch()
    {
        var directory = Environment.CurrentDirectory;
        var options = new PowerShellRuntimeLaunchAdapter().CreateOptions(
            new RuntimeLaunchContext(directory, new TerminalSize(90, 28)));

        Assert.EndsWith("powershell.exe", options.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["-NoLogo"], options.Arguments);
        Assert.Equal(directory, options.WorkingDirectory);
        Assert.Equal(new TerminalSize(90, 28), options.InitialSize);
    }

    [Fact]
    public void Cancellation_is_checked_before_launch_validation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new PowerShellRuntimeLaunchAdapter().CreateOptions(
            new RuntimeLaunchContext("missing", new TerminalSize(80, 24)), cancellation.Token));
    }
}
