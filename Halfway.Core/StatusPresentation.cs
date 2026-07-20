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
}
