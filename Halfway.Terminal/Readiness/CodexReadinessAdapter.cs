using System.Text.RegularExpressions;

namespace Halfway.Terminal.Readiness;

public sealed partial class CodexReadinessAdapter : IProcessReadinessAdapter
{
    public static ProcessReadinessAdapterIdentity AdapterIdentity { get; } = new("codex", 1);

    private string _tail = string.Empty;
    private bool _codexObserved;

    public ProcessReadinessAdapterIdentity Identity => AdapterIdentity;

    public bool IsReadyForInput { get; private set; }

    public void ObserveOutput(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        _tail = (_tail + output)[Math.Max(0, _tail.Length + output.Length - 2048)..];
        var plainText = AnsiSequence().Replace(_tail, string.Empty);
        _codexObserved |= plainText.Contains("codex", StringComparison.OrdinalIgnoreCase);

        // This heuristic is deliberately isolated: Codex's terminal presentation may change.
        IsReadyForInput = _codexObserved &&
            (plainText.Contains('›') || plainText.Contains("> ", StringComparison.Ordinal));
    }

    public void ObserveInputSubmitted()
    {
        IsReadyForInput = false;
        _tail = string.Empty;
    }

    [GeneratedRegex("\\x1B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiSequence();
}
