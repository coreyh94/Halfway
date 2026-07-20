namespace Halfway.Core;

public static class StatusPresentation
{
    public static string Glyph(AgentStatus status) => status switch
    {
        AgentStatus.Running => "●",
        AgentStatus.Waiting => "◐",
        AgentStatus.Completed => "✓",
        AgentStatus.Failed or AgentStatus.Disconnected => "!",
        _ => "○",
    };

    /// <summary>
    /// Semantic brush resource key for a status colour. Shared by every surface that
    /// tints a status (sidebar rows, sub-agent tabs, terminal headers) so the colour
    /// vocabulary stays a single system rather than being duplicated per view.
    /// </summary>
    public static string ColorKey(AgentStatus status) => status switch
    {
        AgentStatus.Running => "RunningBrush",
        AgentStatus.Waiting => "WaitingBrush",
        AgentStatus.Completed => "CompletedBrush",
        AgentStatus.Failed => "ErrorBrush",
        _ => "MutedTextBrush",
    };
}
