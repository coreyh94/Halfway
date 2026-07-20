using Halfway.Terminal.Readiness;
using Xunit;

namespace Halfway.Terminal.Tests;

public sealed class ReadinessAdapterTests
{
    [Fact]
    public void KnownAdaptersHaveStableVersionedIdentitiesAndCanBeSelected()
    {
        var shell = ProcessReadinessAdapterCatalog.Create("shell", 1);
        var codex = ProcessReadinessAdapterCatalog.Create("codex", 1);

        Assert.IsType<ShellReadinessAdapter>(shell);
        Assert.Equal(new ProcessReadinessAdapterIdentity("shell", 1), shell.Identity);
        Assert.Equal("shell/v1", shell.Identity.ToString());
        Assert.IsType<CodexReadinessAdapter>(codex);
        Assert.Equal(new ProcessReadinessAdapterIdentity("codex", 1), codex.Identity);
        Assert.Equal("codex/v1", codex.Identity.ToString());
    }

    [Theory]
    [InlineData("shell", 2)]
    [InlineData("codex", 2)]
    [InlineData("unknown", 1)]
    public void UnsupportedAdapterIdentityOrVersionFailsWithoutFallback(string identifier, int version)
    {
        var exception = Assert.Throws<NotSupportedException>(() => ProcessReadinessAdapterCatalog.Create(identifier, version));

        Assert.Contains(identifier, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"v{version}", exception.Message, StringComparison.Ordinal);
    }

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

    [Fact]
    public void Readiness_resets_after_input_and_can_be_observed_again()
    {
        var shell = new ShellReadinessAdapter(); shell.ObserveOutput("PS> "); shell.ObserveInputSubmitted(); Assert.False(shell.IsReadyForInput);
        shell.ObserveOutput("PS> "); Assert.True(shell.IsReadyForInput);

        var codex = new CodexReadinessAdapter(); codex.ObserveOutput("Codex CLI\r\n> "); codex.ObserveInputSubmitted(); Assert.False(codex.IsReadyForInput);
        codex.ObserveOutput("> "); Assert.True(codex.IsReadyForInput);
    }

    [Fact]
    public void CodexAnsiPromptCanBeDetectedAcrossSplitEscapeAndTextChunks()
    {
        var readiness = new CodexReadinessAdapter();

        readiness.ObserveOutput("Co");
        readiness.ObserveOutput("dex CLI\r\n\x1b[3");
        readiness.ObserveOutput("2m> \x1b[0m");

        Assert.True(readiness.IsReadyForInput);
    }
}
