using System.Text;
using Halfway.Terminal.Readiness;
using Halfway.Terminal.Windows;
using Xunit;

namespace Halfway.Terminal.Tests;

public sealed class Utf8OutputDecoderTests
{
    public static TheoryData<string> MultibyteCharacters => new()
    {
        "\u00A2",
        "\u20AC",
        "\U0001F642",
        "\u203A",
    };

    [Theory]
    [MemberData(nameof(MultibyteCharacters))]
    public void SplitUtf8CharactersAreEmittedExactlyOnceAtEveryByteBoundary(string character)
    {
        var bytes = Encoding.UTF8.GetBytes(character);
        for (var split = 1; split < bytes.Length; split++)
        {
            var decoder = new Utf8OutputDecoder();
            var first = decoder.Decode(bytes.AsSpan(0, split));
            var second = decoder.Decode(bytes.AsSpan(split));
            var final = decoder.Decode([], flush: true);

            Assert.Equal(character, first + second + final);
        }
    }

    [Fact]
    public void SplitCodexPromptReachesReadinessAsTheSameIntactCharacter()
    {
        var bytes = Encoding.UTF8.GetBytes("Codex CLI\r\n\u203A ");
        var promptBytes = Encoding.UTF8.GetBytes("\u203A");
        var promptStart = Array.IndexOf(bytes, promptBytes[0]);

        for (var split = promptStart + 1; split < promptStart + promptBytes.Length; split++)
        {
            var decoder = new Utf8OutputDecoder();
            var readiness = new CodexReadinessAdapter();
            var output = decoder.Decode(bytes.AsSpan(0, split));
            readiness.ObserveOutput(output);
            var remainder = decoder.Decode(bytes.AsSpan(split)) + decoder.Decode([], flush: true);
            readiness.ObserveOutput(remainder);

            Assert.Equal("Codex CLI\r\n\u203A ", output + remainder);
            Assert.True(readiness.IsReadyForInput);
        }
    }

    [Fact]
    public void IncompleteFinalSequenceIsFlushedOnceAtEndOfStream()
    {
        var bytes = Encoding.UTF8.GetBytes("\u20AC");
        var decoder = new Utf8OutputDecoder();

        Assert.Equal(string.Empty, decoder.Decode(bytes.AsSpan(0, 2)));
        Assert.Equal("\uFFFD", decoder.Decode([], flush: true));
        Assert.Equal(string.Empty, decoder.Decode([], flush: true));
    }
}
