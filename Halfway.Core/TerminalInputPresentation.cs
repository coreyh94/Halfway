namespace Halfway.Core;

public readonly record struct TerminalInputAcceptance(bool Accepted)
{
    public static TerminalInputAcceptance AcceptedSubmission { get; } = new(true);
    public static TerminalInputAcceptance RejectedSubmission { get; } = new(false);
}

public static class TerminalInputPresentation
{
    public static string ResolveVisibleText(string submittedText, string currentText, TerminalInputAcceptance acceptance)
    {
        ArgumentNullException.ThrowIfNull(submittedText);
        ArgumentNullException.ThrowIfNull(currentText);
        return acceptance.Accepted && string.Equals(currentText, submittedText, StringComparison.Ordinal)
            ? string.Empty
            : currentText;
    }
}
