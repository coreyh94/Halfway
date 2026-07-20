using Halfway.Terminal.Readiness;
using Xunit;

namespace Halfway.Terminal.Tests;

public sealed class ReadinessAdapterTests
{
    [Fact]
    public void Codex_readiness_waits_for_identity_and_prompt_across_output_chunks()
    {
        var readiness = new CodexReadinessAdapter();

        readiness.ObserveOutput("Codex CLI\r\n");
        Assert.False(readiness.IsReadyForInput);

        readiness.ObserveOutput("\x1b[32m> \x1b[0m");

        Assert.True(readiness.IsReadyForInput);
    }

    [Fact]
    public void Shell_readiness_requires_nonempty_output()
    {
        var readiness = new ShellReadinessAdapter();

        readiness.ObserveOutput(string.Empty);
        Assert.False(readiness.IsReadyForInput);

        readiness.ObserveOutput("PS> ");

        Assert.True(readiness.IsReadyForInput);
    }
}
