using Halfway.Terminal;
using Halfway.Terminal.Readiness;
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

    [Theory]
    [InlineData(null, typeof(PowerShellRuntimeLaunchAdapter))]
    [InlineData("", typeof(PowerShellRuntimeLaunchAdapter))]
    [InlineData("powershell", typeof(PowerShellRuntimeLaunchAdapter))]
    [InlineData(" CODEX ", typeof(CodexRuntimeLaunchAdapter))]
    public void Launch_profile_selection_is_explicit(string? profile, Type expectedType)
    {
        Assert.IsType(expectedType, RuntimeLaunchAdapterSelection.Create(profile));
    }

    [Fact]
    public void Unknown_launch_profile_fails_before_session_start()
    {
        var exception = Assert.Throws<ArgumentException>(() => RuntimeLaunchAdapterSelection.Create("wsl"));

        Assert.Contains("powershell", exception.Message, StringComparison.Ordinal);
        Assert.Contains("codex", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RuntimeLaunchProfile.PowerShell, typeof(ShellReadinessAdapter), "shell", 1)]
    [InlineData(RuntimeLaunchProfile.Codex, typeof(CodexReadinessAdapter), "codex", 1)]
    public void LaunchProfileSelectsVersionedReadinessAdapter(RuntimeLaunchProfile profile, Type expectedType, string identifier, int version)
    {
        var adapter = RuntimeReadinessAdapterSelection.Create(profile);

        Assert.IsType(expectedType, adapter);
        Assert.Equal(new ProcessReadinessAdapterIdentity(identifier, version), adapter.Identity);
    }
}
