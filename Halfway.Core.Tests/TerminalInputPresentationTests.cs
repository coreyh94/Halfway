using Halfway.Core;
using Xunit;

namespace Halfway.Core.Tests;

public sealed class TerminalInputPresentationTests
{
    [Fact]
    public void QueueRejectionRetainsOriginalTextAndPartialInputBlocking()
    {
        var visible = TerminalInputPresentation.ResolveVisibleText("build", "build", TerminalInputAcceptance.RejectedSubmission);

        Assert.Equal("build", visible);
        Assert.False(string.IsNullOrEmpty(visible));
    }

    [Fact]
    public void SuccessfulAcceptanceClearsOnlyUnchangedAcceptedText()
    {
        Assert.Equal(string.Empty, TerminalInputPresentation.ResolveVisibleText("build", "build", TerminalInputAcceptance.AcceptedSubmission));
        Assert.Equal("build now", TerminalInputPresentation.ResolveVisibleText("build", "build now", TerminalInputAcceptance.AcceptedSubmission));
    }

    [Fact]
    public void LateRejectionNeverOverwritesNewerEdits()
    {
        Assert.Equal("newer edit", TerminalInputPresentation.ResolveVisibleText("build", "newer edit", TerminalInputAcceptance.RejectedSubmission));
    }
}
