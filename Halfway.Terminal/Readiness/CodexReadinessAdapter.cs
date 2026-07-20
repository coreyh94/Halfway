using System.Text.RegularExpressions;

namespace Halfway.Terminal.Readiness;

public sealed partial class CodexReadinessAdapter : IProcessReadinessAdapter
{
    private string _tail = string.Empty;
    private bool _codexObserved;

    public bool IsReadyForInput { get; private set; }

    public void ObserveOutput(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        var plainText = AnsiSequence().Replace(output, string.Empty);
        _tail = (_tail + plainText)[Math.Max(0, _tail.Length + plainText.Length - 2048)..];
        _codexObserved |= plainText.Contains("codex", StringComparison.OrdinalIgnoreCase);

        // This heuristic is deliberately isolated: Codex's terminal presentation may change.
        IsReadyForInput = _codexObserved &&
            (_tail.Contains('›') || _tail.Contains("> ", StringComparison.Ordinal));
    }

    public void ObserveInputSubmitted()
    {
        IsReadyForInput = false;
        _tail = string.Empty;
    }

    [GeneratedRegex("\\x1B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiSequence();
}
