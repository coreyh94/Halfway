using System.Text;

namespace Halfway.Terminal.Windows;

internal sealed class Utf8OutputDecoder
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    public string Decode(ReadOnlySpan<byte> bytes, bool flush = false)
    {
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = _decoder.GetChars(bytes, characters, flush);
        return count == 0 ? string.Empty : new string(characters, 0, count);
    }
}
