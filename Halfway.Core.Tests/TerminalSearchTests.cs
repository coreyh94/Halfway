using Halfway.Core;
using Xunit;

namespace Halfway.Core.Tests;

public sealed class TerminalSearchTests
{
    [Fact]
    public void FindsCaseInsensitiveNonOverlappingMatchesInDisplayOrder()
    {
        Assert.Equal([0, 8, 16], TerminalSearch.FindMatches("Ready...READY...ready", "ready"));
        Assert.Equal([0, 2], TerminalSearch.FindMatches("aaaa", "aa"));
    }

    [Fact]
    public void EmptyTextOrQueryHasNoMatches()
    {
        Assert.Empty(TerminalSearch.FindMatches(string.Empty, "ready"));
        Assert.Empty(TerminalSearch.FindMatches("ready", string.Empty));
    }

    [Fact]
    public void NextAndPreviousMatchesWrap()
    {
        Assert.Equal(1, TerminalSearch.Move(3, 0, 1));
        Assert.Equal(0, TerminalSearch.Move(3, 2, 1));
        Assert.Equal(2, TerminalSearch.Move(3, 0, -1));
        Assert.Equal(0, TerminalSearch.Move(3, 1, -1));
    }

    [Fact]
    public void EmptyAndUnselectedMatchSetsAreDeterministic()
    {
        Assert.Equal(-1, TerminalSearch.Move(0, -1, 1));
        Assert.Equal(0, TerminalSearch.Move(2, -1, 1));
        Assert.Equal(1, TerminalSearch.Move(2, -1, -1));
    }
}
